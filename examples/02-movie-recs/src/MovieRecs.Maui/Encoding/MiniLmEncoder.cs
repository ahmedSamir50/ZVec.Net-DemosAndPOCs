using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MovieRecs.Maui.Options;

namespace MovieRecs.Maui.Encoding;

public interface IMiniLmEncoder
{
    bool IsReady { get; }
    string? LastError { get; }
    Task EnsureLoadedAsync(CancellationToken ct = default);
    float[] Embed(string text);
}

/// <summary>
/// all-MiniLM-L6-v2 via ONNX Runtime: Bert tokenize → mean-pool (if needed) → L2 → 384-d.
/// </summary>
/// <remarks>
/// Model + vocab are MauiAssets, copied to <see cref="FileSystem.CacheDirectory"/> because
/// Android may compress package files — ONNX Runtime needs a real filesystem path / bytes.
/// </remarks>
public sealed class MiniLmEncoder : IMiniLmEncoder, IDisposable
{
    private readonly MovieRecsOptions _options;
    // Single session load under concurrent UI EnsureLoadedAsync calls.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private BertWordPieceTokenizer? _tokenizer;
    private string? _inputIdsName;
    private string? _attentionMaskName;
    private string? _tokenTypeIdsName;
    private bool _loaded;

    public MiniLmEncoder(MovieRecsOptions options)
    {
        _options = options;
    }

    public bool IsReady => _loaded && _session is not null && _tokenizer is not null;
    public string? LastError { get; private set; }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (IsReady)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady)
                return;

            var cacheDir = Path.Combine(FileSystem.CacheDirectory, "models");
            Directory.CreateDirectory(cacheDir);

            var onnxPath = await CopyAssetToCacheAsync(MovieRecsOptions.OnnxAssetPath, cacheDir, ct)
                .ConfigureAwait(false);
            var vocabPath = await CopyAssetToCacheAsync(MovieRecsOptions.VocabAssetPath, cacheDir, ct)
                .ConfigureAwait(false);

            _tokenizer = new BertWordPieceTokenizer(vocabPath);

            var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
            };
            _session = new InferenceSession(onnxPath, so);

            ResolveInputNames(_session);
            _loaded = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _loaded = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public float[] Embed(string text)
    {
        if (!IsReady || _session is null || _tokenizer is null)
            throw new InvalidOperationException("MiniLM encoder is not loaded. Call EnsureLoadedAsync first.");

        text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        var maxLen = Math.Clamp(_options.MaxSequenceLength, 32, 256);
        var (inputIds, attention) = _tokenizer.Encode(text, maxLen);
        // Single-sentence MiniLM: segment ids are all zeros. Some ONNX exports still require the input.
        var tokenTypes = new long[maxLen];

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName!,
                new DenseTensor<long>(inputIds, [1, maxLen])),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName!,
                new DenseTensor<long>(attention, [1, maxLen]))
        };
        if (_tokenTypeIdsName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName,
                new DenseTensor<long>(tokenTypes, [1, maxLen])));
        }

        using var results = _session.Run(inputs);
        // Prefer pooled sentence_embedding when the export includes it; else mean-pool last_hidden_state [seq, 384].
        var preferred = results.FirstOrDefault(r =>
            r.Name.Contains("sentence", StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains("pool", StringComparison.OrdinalIgnoreCase)) ?? results.First();
        var output = preferred.AsEnumerable<float>().ToArray();

        float[] pooled;
        if (output.Length == MovieRecsOptions.EmbeddingDim)
        {
            pooled = output;
        }
        else if (output.Length % MovieRecsOptions.EmbeddingDim == 0)
        {
            var seq = output.Length / MovieRecsOptions.EmbeddingDim;
            var mask = attention.Length >= seq ? attention.AsSpan(0, seq).ToArray() : Enumerable.Repeat(1L, seq).ToArray();
            pooled = MeanPool(output, mask, seq, MovieRecsOptions.EmbeddingDim);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unexpected ONNX output length {output.Length}; expected multiple of {MovieRecsOptions.EmbeddingDim}.");
        }

        // Unit length → ZVec Cosine distance ≈ 1 − cosθ stays meaningful.
        return VectorMath.L2Normalize(pooled);
    }

    /// <summary>Average token hidden states where attention==1 (skip pads).</summary>
    private static float[] MeanPool(float[] hidden, long[] attention, int seqLen, int dim)
    {
        var sum = new double[dim];
        double count = 0;
        for (var t = 0; t < seqLen; t++)
        {
            if (attention[t] == 0)
                continue;
            var offset = t * dim;
            for (var d = 0; d < dim; d++)
                sum[d] += hidden[offset + d];
            count++;
        }

        if (count < 1)
            count = 1;

        var mean = new float[dim];
        for (var d = 0; d < dim; d++)
            mean[d] = (float)(sum[d] / count);
        return mean;
    }

    private void ResolveInputNames(InferenceSession session)
    {
        var names = session.InputMetadata.Keys.ToList();
        _inputIdsName = names.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
        _attentionMaskName = names.FirstOrDefault(n => n.Contains("attention", StringComparison.OrdinalIgnoreCase));
        _tokenTypeIdsName = names.FirstOrDefault(n =>
            n.Contains("token_type", StringComparison.OrdinalIgnoreCase)
            || n.Contains("type_ids", StringComparison.OrdinalIgnoreCase));

        if (_inputIdsName is null || _attentionMaskName is null)
            throw new InvalidOperationException(
                "ONNX model must expose input_ids and attention_mask inputs. Found: " + string.Join(", ", names));
    }

    private static async Task<string> CopyAssetToCacheAsync(string assetPath, string cacheDir, CancellationToken ct)
    {
        var fileName = Path.GetFileName(assetPath);
        var dest = Path.Combine(cacheDir, fileName);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            return dest;

        await using var src = await FileSystem.OpenAppPackageFileAsync(assetPath).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return dest;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _gate.Dispose();
    }
}
