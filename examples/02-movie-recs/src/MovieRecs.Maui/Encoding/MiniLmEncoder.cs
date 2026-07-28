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
/// all-MiniLM-L6-v2 via ONNX Runtime: Bert tokenize → sentence-transformers mean-pool → L2 → 384-d.
/// </summary>
/// <remarks>
/// Model + vocab are MauiAssets, copied to <see cref="FileSystem.CacheDirectory"/> because
/// Android may compress package files — ONNX Runtime needs a real filesystem path / bytes.
/// Never use BERT <c>pooler_output</c>; ST embeddings are mean-pooled <c>last_hidden_state</c>
/// (or an export's <c>sentence_embedding</c>).
/// </remarks>
public sealed class MiniLmEncoder : IMiniLmEncoder, IDisposable
{
    private readonly MovieRecsOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private BertWordPieceTokenizer? _tokenizer;
    private string? _inputIdsName;
    private string? _attentionMaskName;
    private string? _tokenTypeIdsName;
    private bool _loaded;
    private bool _sanityChecked;

    public MiniLmEncoder(MovieRecsOptions options)
    {
        _options = options;
    }

    public bool IsReady => _loaded && _session is not null && _tokenizer is not null;
    public string? LastError { get; private set; }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (IsReady)
        {
            EnsureSanityOnce();
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                EnsureSanityOnce();
                return;
            }

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
            try
            {
                EnsureSanityOnce();
            }
            catch
            {
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
                _loaded = false;
                throw;
            }
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
        var pooled = PoolSentenceEmbedding(results, attention);
        return VectorMath.L2Normalize(pooled);
    }

    /// <summary>
    /// Sentence-transformers path: <c>sentence_embedding</c> if present; else mean-pool
    /// <c>last_hidden_state</c>. Never BERT <c>pooler_output</c> (wrong space → joke neighbors).
    /// </summary>
    private static float[] PoolSentenceEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, long[] attention)
    {
        var list = results.ToList();
        var sentence = list.FirstOrDefault(r =>
            r.Name.Contains("sentence_embedding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.Name, "sentence_embedding", StringComparison.OrdinalIgnoreCase));

        if (sentence is not null)
        {
            var vec = sentence.AsEnumerable<float>().ToArray();
            if (vec.Length == MovieRecsOptions.EmbeddingDim)
                return vec;
            if (vec.Length % MovieRecsOptions.EmbeddingDim == 0)
            {
                var seq = vec.Length / MovieRecsOptions.EmbeddingDim;
                return MeanPool(vec, AttentionMaskForSeq(attention, seq), seq, MovieRecsOptions.EmbeddingDim);
            }
        }

        // Prefer last_hidden_state (token sequence). Explicitly skip pooler_output.
        var hidden = list.FirstOrDefault(r =>
            r.Name.Contains("last_hidden", StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains("hidden_state", StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains("token_embeddings", StringComparison.OrdinalIgnoreCase));

        hidden ??= list.FirstOrDefault(r =>
            !r.Name.Contains("pooler", StringComparison.OrdinalIgnoreCase)
            && r.AsEnumerable<float>().Count() > MovieRecsOptions.EmbeddingDim);

        if (hidden is null)
        {
            // Last resort: any non-pooler output.
            hidden = list.FirstOrDefault(r => !r.Name.Contains("pooler", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException(
                         "ONNX outputs: " + string.Join(", ", list.Select(r => r.Name))
                         + " — need sentence_embedding or last_hidden_state (not pooler_output).");
        }

        if (hidden.Name.Contains("pooler", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing BERT pooler_output ('{hidden.Name}'). Use mean-pooled last_hidden_state.");
        }

        var output = hidden.AsEnumerable<float>().ToArray();
        if (output.Length == MovieRecsOptions.EmbeddingDim)
            return output;

        if (output.Length % MovieRecsOptions.EmbeddingDim != 0)
        {
            throw new InvalidOperationException(
                $"Unexpected ONNX output '{hidden.Name}' length {output.Length}; expected multiple of {MovieRecsOptions.EmbeddingDim}.");
        }

        var seqLen = output.Length / MovieRecsOptions.EmbeddingDim;
        return MeanPool(output, AttentionMaskForSeq(attention, seqLen), seqLen, MovieRecsOptions.EmbeddingDim);
    }

    private static long[] AttentionMaskForSeq(long[] attention, int seqLen) =>
        attention.Length >= seqLen
            ? attention.AsSpan(0, seqLen).ToArray()
            : Enumerable.Repeat(1L, seqLen).ToArray();

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

    /// <summary>
    /// Once per process: Inception-like text must be closer to sci-fi than to kids/comedy.
    /// Catches pooler_output / broken tokenize regressions before we build a joke index.
    /// </summary>
    private void EnsureSanityOnce()
    {
        if (_sanityChecked || !IsReady)
            return;

        var inception = Embed("Movie title: Inception (2010). Genres: Action Sci-Fi Thriller.");
        var scifi = Embed("Movie title: Interstellar (2014). Genres: Adventure Drama Sci-Fi.");
        var kids = Embed("Movie title: Babies (2010). Genres: Documentary.");

        var cosGood = VectorMath.Dot(inception, scifi);
        var cosBad = VectorMath.Dot(inception, kids);
        if (cosGood < cosBad + 0.05)
        {
            var msg =
                $"MiniLM sanity failed: cos(Inception,Interstellar)={cosGood:F3} is not clearly above cos(Inception,Babies)={cosBad:F3}. " +
                "Check ONNX pooling (must not use pooler_output).";
            LastError = msg;
            throw new InvalidOperationException(msg);
        }

        _sanityChecked = true;
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
