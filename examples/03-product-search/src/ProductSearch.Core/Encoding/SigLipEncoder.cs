using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;

namespace ProductSearch.Core.Encoding;

public sealed class SigLipEncoder : ISigLipEncoder, IDisposable
{
    private readonly ProductSearchOptions _options;
    private readonly ILogger<SigLipEncoder> _logger;
    private readonly object _gate = new();
    private InferenceSession? _vision;
    private InferenceSession? _text;
    private SigLipTokenizer? _tokenizer;
    private int _embeddingDim = 768;
    private int _imageSize = 224;
    private bool _useBilinearResize;
    private string? _activeModelId;

    public SigLipEncoder(IOptions<ProductSearchOptions> options, ILogger<SigLipEncoder> logger)
    {
        _options = options.Value;
        _logger = logger;
        NotReadyReason = "SigLIP models not loaded yet (waiting for bootstrap).";
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

    public int ImageSize
    {
        get { lock (_gate) return _imageSize; }
    }

    public void InitializeFromDisk(string modelsDir, SigLipModelDefinition model)
    {
        lock (_gate)
        {
            ValidateRequiredFiles(modelsDir, model);

            var visionPath = Path.Combine(modelsDir, _options.VisionModelFile);
            var textPath = Path.Combine(modelsDir, _options.TextModelFile);

            var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };

            _vision?.Dispose();
            _text?.Dispose();
            _vision = null;
            _text = null;
            _tokenizer = null;
            IsReady = false;

            try
            {
                _vision = new InferenceSession(visionPath, so);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed loading vision ONNX at {visionPath}.", ex);
            }

            try
            {
                _text = new InferenceSession(textPath, so);
            }
            catch (Exception ex)
            {
                _vision.Dispose();
                _vision = null;
                throw new InvalidOperationException($"Failed loading text ONNX at {textPath}.", ex);
            }

            try
            {
                _tokenizer = new SigLipTokenizer(modelsDir, model);
            }
            catch (Exception ex)
            {
                _vision.Dispose();
                _text.Dispose();
                _vision = null;
                _text = null;
                throw new InvalidOperationException(
                    $"Failed loading tokenizer.json in {modelsDir}.", ex);
            }

            _embeddingDim = model.EmbeddingDim;
            _imageSize = model.ImageSize;
            _useBilinearResize = model.UseBilinearResize;
            _activeModelId = model.Id;
            IsReady = true;
            NotReadyReason = null;
            _logger.LogInformation(
                "SigLIP encoder ready: {ModelId} dim={Dim} size={Size} bilinear={Bilinear} dir={Dir}",
                model.Id, model.EmbeddingDim, model.ImageSize, model.UseBilinearResize, modelsDir);
        }
    }

    private static void ValidateRequiredFiles(string modelsDir, SigLipModelDefinition model)
    {
        foreach (var name in model.RequiredModelFiles)
        {
            var path = Path.Combine(modelsDir, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new FileNotFoundException($"SigLIP model file missing: {path}", path);

            if (SigLipModelCatalog.TryGetExpectedBytes(model, name, out var expected)
                && new FileInfo(path).Length != expected)
            {
                throw new InvalidDataException(
                    $"SigLIP model file size mismatch for {name}: expected {expected} bytes, found {new FileInfo(path).Length} bytes at {path}.");
            }
        }
    }

    public float[] EncodeImage(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return EncodeImage(fs);
    }

    public float[] EncodeImage(Stream imageStream)
    {
        EnsureReady();
        var size = ImageSize;
        var bilinear = _useBilinearResize;
        var pixels = SigLipImagePreprocessor.ToSigLipTensor(imageStream, size, bilinear);
        lock (_gate)
        {
            var inputName = _vision!.InputMetadata.Keys.First();
            var tensor = new DenseTensor<float>(pixels, [1, 3, size, size]);
            using var results = _vision.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            return VectorMath.L2Normalize(ExtractEmbedding(results, _embeddingDim));
        }
    }

    public float[] EncodeText(string text)
    {
        EnsureReady();
        var (ids, mask) = _tokenizer!.Encode(text);
        lock (_gate)
        {
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _text!.InputMetadata.Keys)
            {
                if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(mask, [1, SigLipTokenizer.DefaultContextLength])));
                else
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(ids, [1, SigLipTokenizer.DefaultContextLength])));
            }

            using var results = _text.Run(inputs);
            return VectorMath.L2Normalize(ExtractEmbedding(results, _embeddingDim));
        }
    }

    private static float[] ExtractEmbedding(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int expectedDim)
    {
        foreach (var preferred in new[] { "text_embeds", "image_embeds", "pooler_output", "embeds", "embedding" })
        {
            var hit = results.FirstOrDefault(r => r.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return ToFlatDim(hit, expectedDim);
        }

        return ToFlatDim(results.Last(), expectedDim);
    }

    private static float[] ToFlatDim(DisposableNamedOnnxValue value, int expectedDim)
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

        var flat = tensor.ToArray();
        if (flat.Length == expectedDim)
            return flat;

        throw new InvalidOperationException(
            $"Unexpected ONNX output '{value.Name}' shape [{string.Join(',', dims)}] — expected {expectedDim}-d embedding.");
    }

    private void EnsureReady()
    {
        if (!IsReady)
            throw new InvalidOperationException(NotReadyReason ?? "SigLIP encoder is not ready.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _vision?.Dispose();
            _text?.Dispose();
            _vision = null;
            _text = null;
            _tokenizer = null;
            IsReady = false;
            _activeModelId = null;
        }
    }
}
