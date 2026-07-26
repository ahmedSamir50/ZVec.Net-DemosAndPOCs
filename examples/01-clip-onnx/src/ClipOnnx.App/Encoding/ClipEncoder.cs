using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClipOnnx.App.Encoding;

public interface IClipEncoder
{
    bool IsReady { get; }
    string? NotReadyReason { get; }
    /// <summary>Load ONNX sessions from the configured ModelsDir (called after bootstrap download).</summary>
    void InitializeFromDisk();
    float[] EncodeImage(Stream imageStream);
    float[] EncodeImage(string filePath);
    float[] EncodeText(string text);
}

/// <summary>
/// Dual CLIP encoders → one shared 512-d embedding space (ViT-B/32).
///
/// Vision path:  image → ClipImagePreprocessor (NCHW 224) → vision.onnx → 512-d → L2
/// Text path:    string → ClipTokenizer (77 ids) → text.onnx → 512-d → L2
///
/// Contrastive training aligned image/text pairs so cosine(image_emb, text_emb)
/// is high for matching concepts — enabling text→image and image→image search
/// against the same ZVec Cosine index.
///
/// Sessions load lazily via <see cref="InitializeFromDisk"/> after model bootstrap.
/// </summary>
public sealed class ClipEncoder : IClipEncoder, IDisposable
{
    private readonly ClipOnnxOptions _options;
    private readonly object _gate = new();
    private InferenceSession? _vision;
    private InferenceSession? _text;
    private ClipTokenizer? _tokenizer;

    public ClipEncoder(IOptions<ClipOnnxOptions> options)
    {
        _options = options.Value;
        NotReadyReason = "CLIP models not loaded yet (waiting for bootstrap).";
    }

    public bool IsReady { get; private set; }
    public string? NotReadyReason { get; private set; }

    public void InitializeFromDisk()
    {
        lock (_gate)
        {
            if (IsReady)
                return;

            var models = Path.GetFullPath(_options.ModelsDir);
            var visionPath = Path.Combine(models, _options.VisionModelFile);
            var textPath = Path.Combine(models, _options.TextModelFile);
            var vocabPath = Path.Combine(models, _options.VocabFile);
            var mergesPath = Path.Combine(models, _options.MergesFile);

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
            IsReady = true;
            NotReadyReason = null;
        }
    }

    public float[] EncodeImage(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return EncodeImage(fs);
    }

    /// <summary>
    /// Vision ONNX: input float[1,3,224,224] → pick embedding output → L2 → float[512].
    /// </summary>
    public float[] EncodeImage(Stream imageStream)
    {
        EnsureReady();
        var pixels = ClipImagePreprocessor.ToClipTensor(imageStream);
        lock (_gate)
        {
            // Export naming varies; take the first (usually only) input.
            var inputName = _vision!.InputMetadata.Keys.First();
            var tensor = new DenseTensor<float>(pixels, [1, 3, ClipImagePreprocessor.Size, ClipImagePreprocessor.Size]);
            using var results = _vision.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            return VectorMath.L2Normalize(ExtractEmbedding(results));
        }
    }

    /// <summary>
    /// Text ONNX: input_ids + attention_mask as long[1,77] → embedding → L2 → float[512].
    /// Same geometry as EncodeImage so QueryAsync can mix modalities.
    /// </summary>
    public float[] EncodeText(string text)
    {
        EnsureReady();
        var (ids, mask) = _tokenizer!.Encode(text);
        lock (_gate)
        {
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _text!.InputMetadata.Keys)
            {
                // Bind by name hint: "*mask*" → attention_mask, else token ids.
                if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(mask, [1, ClipTokenizer.ContextLength])));
                else
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(ids, [1, ClipTokenizer.ContextLength])));
            }

            using var results = _text.Run(inputs);
            return VectorMath.L2Normalize(ExtractEmbedding(results));
        }
    }

    /// <summary>
    /// HF/ORT export names differ (embeds, pooler_output, …). Prefer known names, else last output.
    /// </summary>
    private static float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var preferred in new[] { "embeds", "embedding", "pooler_output", "image_embeds", "text_embeds", "last_hidden_state" })
        {
            var hit = results.FirstOrDefault(r => r.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return ToFlat512(hit);
        }

        return ToFlat512(results.Last());
    }

    /// <summary>
    /// Collapse ONNX tensor to a single 512-d row (CLIP ViT-B/32 projection dim).
    /// Handles [1,512], [1,seq,512] (take first token), or flat length ≥ 512.
    /// </summary>
    private static float[] ToFlat512(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<float>();
        var dims = tensor.Dimensions.ToArray();
        if (dims.Length == 2 && dims[1] == 512)
        {
            var row = new float[512];
            for (var i = 0; i < 512; i++)
                row[i] = tensor[0, i];
            return row;
        }

        if (dims.Length == 3 && dims[2] == 512)
        {
            // Sequence output: use position 0 (often CLS / pooled-like for this export).
            var row = new float[512];
            for (var i = 0; i < 512; i++)
                row[i] = tensor[0, 0, i];
            return row;
        }

        var flat = tensor.ToArray();
        if (flat.Length == 512)
            return flat;
        if (flat.Length > 512)
            return flat.AsSpan(0, 512).ToArray();

        throw new InvalidOperationException(
            $"Unexpected ONNX output '{value.Name}' shape [{string.Join(',', dims)}] — expected 512-d embedding.");
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
        }
    }
}
