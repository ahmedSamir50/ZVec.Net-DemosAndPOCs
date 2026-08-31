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
    private readonly object _lifecycleGate = new();
    private readonly object _tokenizerGate = new();
    private readonly object _textRunGate = new();
    private readonly object _visionRunGate = new();
    private InferenceSession? _vision;
    private InferenceSession? _text;
    private SigLipTokenizer? _tokenizer;
    private int _embeddingDim = 768;
    private int _imageSize = 224;
    private bool _useBilinearResize;
    private string? _activeModelId;
    private int _intraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);

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
        get { lock (_lifecycleGate) return _activeModelId; }
    }

    public int EmbeddingDim
    {
        get { lock (_lifecycleGate) return _embeddingDim; }
    }

    public int ImageSize
    {
        get { lock (_lifecycleGate) return _imageSize; }
    }

    public int IntraOpNumThreads
    {
        get { lock (_lifecycleGate) return _intraOpNumThreads; }
    }

    public void InitializeFromDisk(string modelsDir, SigLipModelDefinition model)
    {
        lock (_lifecycleGate)
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
            _intraOpNumThreads = so.IntraOpNumThreads;

            lock (_tokenizerGate)
            lock (_textRunGate)
            lock (_visionRunGate)
            {
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
            }

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
        lock (_visionRunGate)
        {
            var session = _vision ?? throw new InvalidOperationException("Vision session is not loaded.");
            var dim = _embeddingDim;
            var inputName = session.InputMetadata.Keys.First();
            var tensor = new DenseTensor<float>(pixels, [1, 3, size, size]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            return VectorMath.L2Normalize(ExtractEmbedding(results, dim));
        }
    }

    public float[] EncodeText(string text)
    {
        EnsureReady();
        long[] ids;
        long[] mask;
        lock (_tokenizerGate)
        {
            var tokenizer = _tokenizer ?? throw new InvalidOperationException("Tokenizer is not loaded.");
            (ids, mask) = tokenizer.Encode(text);
        }

        lock (_textRunGate)
        {
            var session = _text ?? throw new InvalidOperationException("Text session is not loaded.");
            var dim = _embeddingDim;
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in session.InputMetadata.Keys)
            {
                if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(mask, [1, SigLipTokenizer.DefaultContextLength])));
                else
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(ids, [1, SigLipTokenizer.DefaultContextLength])));
            }

            using var results = session.Run(inputs);
            return VectorMath.L2Normalize(ExtractEmbedding(results, dim));
        }
    }

    public float[][] EncodeTextBatch(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];

        EnsureReady();
        var n = texts.Count;
        var ctx = SigLipTokenizer.DefaultContextLength;
        var allIds = new long[n * ctx];
        var allMask = new long[n * ctx];

        lock (_tokenizerGate)
        {
            var tokenizer = _tokenizer ?? throw new InvalidOperationException("Tokenizer is not loaded.");
            for (var i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (ids, mask) = tokenizer.Encode(texts[i]);
                Array.Copy(ids, 0, allIds, i * ctx, ctx);
                Array.Copy(mask, 0, allMask, i * ctx, ctx);
            }
        }

        lock (_textRunGate)
        {
            var session = _text ?? throw new InvalidOperationException("Text session is not loaded.");
            var dim = _embeddingDim;
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in session.InputMetadata.Keys)
            {
                if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(allMask, [n, ctx])));
                else
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(allIds, [n, ctx])));
            }

            using var results = session.Run(inputs);
            return ExtractEmbeddingRows(results, dim, n);
        }
    }

    public float[][] EncodeImageBatch(IReadOnlyList<string> filePaths, CancellationToken ct = default)
    {
        if (filePaths.Count == 0)
            return [];

        EnsureReady();
        var n = filePaths.Count;
        var size = ImageSize;
        var bilinear = _useBilinearResize;
        var plane = 3 * size * size;
        var tensors = new float[n][];
        var parallelism = Math.Max(1, _options.IngestPreprocessParallelism);

        Parallel.For(0, n, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct
        }, i =>
        {
            tensors[i] = SigLipImagePreprocessor.ToSigLipTensor(filePaths[i], size, bilinear);
        });

        var packed = new float[n * plane];
        for (var i = 0; i < n; i++)
            Array.Copy(tensors[i], 0, packed, i * plane, plane);

        lock (_visionRunGate)
        {
            var session = _vision ?? throw new InvalidOperationException("Vision session is not loaded.");
            var dim = _embeddingDim;
            var inputName = session.InputMetadata.Keys.First();
            var tensor = new DenseTensor<float>(packed, [n, 3, size, size]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            return ExtractEmbeddingRows(results, dim, n);
        }
    }

    private static float[] ExtractEmbedding(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int expectedDim)
    {
        return ToFlatDim(FindEmbeddingOutput(results), expectedDim);
    }

    private static float[][] ExtractEmbeddingRows(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int expectedDim,
        int batchSize)
    {
        var value = FindEmbeddingOutput(results);
        var tensor = value.AsTensor<float>();
        var dims = tensor.Dimensions.ToArray();
        var rows = new float[batchSize][];

        if (dims.Length == 2 && dims[1] == expectedDim && dims[0] == batchSize)
        {
            for (var i = 0; i < batchSize; i++)
            {
                var row = new float[expectedDim];
                for (var j = 0; j < expectedDim; j++)
                    row[j] = tensor[i, j];
                rows[i] = VectorMath.L2Normalize(row);
            }
            return rows;
        }

        var flat = tensor.ToArray();
        if (flat.Length == batchSize * expectedDim)
        {
            for (var i = 0; i < batchSize; i++)
            {
                var row = new float[expectedDim];
                Array.Copy(flat, i * expectedDim, row, 0, expectedDim);
                rows[i] = VectorMath.L2Normalize(row);
            }
            return rows;
        }

        throw new InvalidOperationException(
            $"Unexpected ONNX output '{value.Name}' shape [{string.Join(',', dims)}] — expected [{batchSize},{expectedDim}].");
    }

    private static DisposableNamedOnnxValue FindEmbeddingOutput(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var preferred in new[] { "text_embeds", "image_embeds", "pooler_output", "embeds", "embedding" })
        {
            var hit = results.FirstOrDefault(r => r.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit;
        }

        return results.Last();
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
        lock (_lifecycleGate)
        lock (_tokenizerGate)
        lock (_textRunGate)
        lock (_visionRunGate)
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
