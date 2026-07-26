using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClipOnnx.App.Encoding;

public interface IClipEncoder
{
    bool IsReady { get; }
    string? NotReadyReason { get; }
    /// <summary>Active catalog model id (e.g. clip-vit-b16), or null before first load.</summary>
    string? ActiveModelId { get; }
    /// <summary>Embedding dimension for the loaded dual-encoder (512 or 768).</summary>
    int EmbeddingDim { get; }
    /// <summary>Load ONNX sessions from a per-model directory after download/bootstrap.</summary>
    void InitializeFromDisk(string modelsDir, ClipModelDefinition model);
    float[] EncodeImage(Stream imageStream);
    float[] EncodeImage(string filePath);
    float[] EncodeText(string text);
}

/// <summary>
/// Dual CLIP encoders → one shared embedding space (dim depends on active model: 512 or 768).
///
/// Vision path:  image → ClipImagePreprocessor (NCHW 224 center-crop) → vision.onnx → L2
/// Text path:    string → ClipTokenizer (77 ids) → text.onnx → L2
///
/// Contrastive training aligned image/text pairs so cosine(image_emb, text_emb)
/// is high for matching concepts — enabling text→image and image→image search
/// against the same ZVec Cosine index.
///
/// Sessions load via <see cref="InitializeFromDisk"/> after model bootstrap / UI model select.
/// Never mix vectors from different model ids in one ZVec collection.
/// </summary>
public sealed class ClipEncoder : IClipEncoder, IDisposable
{
    private readonly ClipOnnxOptions _options;
    private readonly ILogger<ClipEncoder> _logger;
    private readonly object _gate = new();
    private InferenceSession? _vision;
    private InferenceSession? _text;
    private ClipTokenizer? _tokenizer;
    private int _embeddingDim = 512;
    private string? _activeModelId;

    public ClipEncoder(IOptions<ClipOnnxOptions> options, ILogger<ClipEncoder> logger)
    {
        _options = options.Value;
        _logger = logger;
        NotReadyReason = "CLIP models not loaded yet (waiting for bootstrap).";
    }

    public bool IsReady { get; private set; }
    public string? NotReadyReason { get; private set; }
    public string? ActiveModelId
    {
        get { lock (_gate) return _activeModelId; }
    }

    public int EmbeddingDim
    {
        get { lock (_gate) return _embeddingDim; }
    }

    /// <summary>
    /// Load vision+text ONNX + tokenizer from <paramref name="modelsDir"/> for <paramref name="model"/>.
    /// Replaces any previously loaded sessions (hot-swap after UI model select).
    /// </summary>
    public void InitializeFromDisk(string modelsDir, ClipModelDefinition model)
    {
        lock (_gate)
        {
            var visionPath = Path.Combine(modelsDir, _options.VisionModelFile);
            var textPath = Path.Combine(modelsDir, _options.TextModelFile);
            var vocabPath = Path.Combine(modelsDir, _options.VocabFile);
            var mergesPath = Path.Combine(modelsDir, _options.MergesFile);

            foreach (var path in new[] { visionPath, textPath, vocabPath, mergesPath })
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    NotReadyReason = $"CLIP model file missing: {path}";
                    throw new FileNotFoundException(NotReadyReason, path);
                }
            }

            var so = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };

            _vision?.Dispose();
            _text?.Dispose();

            _vision = new InferenceSession(visionPath, so);
            _text = new InferenceSession(textPath, so);
            _tokenizer = new ClipTokenizer(vocabPath, mergesPath);
            _embeddingDim = model.EmbeddingDim;
            _activeModelId = model.Id;

            LogSessionIo("vision", _vision);
            LogSessionIo("text", _text);

            IsReady = true;
            NotReadyReason = null;
            _logger.LogInformation(
                "CLIP encoder ready: {ModelId} dim={Dim} dir={Dir}",
                model.Id, model.EmbeddingDim, modelsDir);
        }
    }

    private void LogSessionIo(string label, InferenceSession session)
    {
        foreach (var (name, meta) in session.InputMetadata)
            _logger.LogInformation("CLIP {Label} input {Name} dims=[{Dims}]", label, name, string.Join(',', meta.Dimensions));
        foreach (var (name, meta) in session.OutputMetadata)
            _logger.LogInformation("CLIP {Label} output {Name} dims=[{Dims}]", label, name, string.Join(',', meta.Dimensions));
    }

    public float[] EncodeImage(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return EncodeImage(fs);
    }

    /// <summary>
    /// Vision ONNX: input float[1,3,224,224] → pick embedding output → L2 → float[dim].
    /// </summary>
    public float[] EncodeImage(Stream imageStream)
    {
        EnsureReady();
        var pixels = ClipImagePreprocessor.ToClipTensor(imageStream);
        lock (_gate)
        {
            var inputName = _vision!.InputMetadata.Keys.First();
            var tensor = new DenseTensor<float>(pixels, [1, 3, ClipImagePreprocessor.Size, ClipImagePreprocessor.Size]);
            using var results = _vision.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            return VectorMath.L2Normalize(ExtractEmbedding(results, sequenceIndex: 0, _embeddingDim));
        }
    }

    /// <summary>
    /// Text ONNX: input_ids + attention_mask as long[1,77] → embedding → L2 → float[dim].
    /// Same geometry as EncodeImage so QueryAsync can mix modalities.
    /// </summary>
    public float[] EncodeText(string text)
    {
        EnsureReady();
        var (ids, mask) = _tokenizer!.Encode(text);
        var eotIndex = ClipTokenizer.FindEotIndex(ids);
        lock (_gate)
        {
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _text!.InputMetadata.Keys)
            {
                if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(mask, [1, ClipTokenizer.ContextLength])));
                else
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(ids, [1, ClipTokenizer.ContextLength])));
            }

            using var results = _text.Run(inputs);
            return VectorMath.L2Normalize(ExtractEmbedding(results, sequenceIndex: eotIndex, _embeddingDim));
        }
    }

    /// <summary>
    /// Prefer pooled *embeds outputs. For sequence tensors, use <paramref name="sequenceIndex"/>
    /// (CLS=0 for vision; EOT index for text). Expects projection dim == <paramref name="expectedDim"/>.
    /// </summary>
    private static float[] ExtractEmbedding(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int sequenceIndex,
        int expectedDim)
    {
        foreach (var preferred in new[] { "embeds", "embedding", "pooler_output", "image_embeds", "text_embeds" })
        {
            var hit = results.FirstOrDefault(r => r.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return ToFlatDim(hit, sequenceIndex, expectedDim);
        }

        var lhs = results.FirstOrDefault(r => r.Name.Contains("last_hidden_state", StringComparison.OrdinalIgnoreCase));
        if (lhs is not null)
            return ToFlatDim(lhs, sequenceIndex, expectedDim);

        return ToFlatDim(results.Last(), sequenceIndex, expectedDim);
    }

    /// <summary>
    /// Collapse ONNX tensor to a single row of <paramref name="expectedDim"/> (512 or 768).
    /// Refuses silent truncation of wrong shapes (e.g. pre-projection 768 hidden into 512).
    /// </summary>
    private static float[] ToFlatDim(DisposableNamedOnnxValue value, int sequenceIndex, int expectedDim)
    {
        var tensor = value.AsTensor<float>();
        var dims = tensor.Dimensions.ToArray();
        if (dims.Length == 2 && dims[1] == expectedDim)
        {
            var row = new float[expectedDim];
            for (var i = 0; i < expectedDim; i++)
                row[i] = tensor[0, i];
            return row;
        }

        if (dims.Length == 3 && dims[2] == expectedDim)
        {
            var seqLen = dims[1];
            var idx = Math.Clamp(sequenceIndex, 0, Math.Max(0, seqLen - 1));
            var row = new float[expectedDim];
            for (var i = 0; i < expectedDim; i++)
                row[i] = tensor[0, idx, i];
            return row;
        }

        var flat = tensor.ToArray();
        if (flat.Length == expectedDim)
            return flat;

        throw new InvalidOperationException(
            $"Unexpected ONNX output '{value.Name}' shape [{string.Join(',', dims)}] — expected {expectedDim}-d embedding (no silent truncate).");
    }

    private void EnsureReady()
    {
        if (!IsReady)
            throw new InvalidOperationException(NotReadyReason ?? "CLIP encoder is not ready.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _vision?.Dispose();
            _text?.Dispose();
            _vision = null;
            _text = null;
            IsReady = false;
            _activeModelId = null;
        }
    }
}
