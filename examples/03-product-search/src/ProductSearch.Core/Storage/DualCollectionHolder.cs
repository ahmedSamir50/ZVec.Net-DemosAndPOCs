using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZVec.NET;
using ZVec.NET.Query;

namespace ProductSearch.Core.Storage;

/// <summary>
/// Holds text + image ZVec collections per SigLIP model.
/// Uses SDK lifecycle: <see cref="IDisposable.Dispose"/> closes (releases LOCK),
/// <see cref="IZvecCollection{T}.Destroy"/> wipes on-disk data, then close.
/// </summary>
public sealed class DualCollectionHolder : IDisposable
{
    private static readonly int[] ReopenBackoffMs = [50, 150, 400];

    private readonly IZvecFactory _factory;
    private readonly ProductSearchOptions _options;
    private readonly IIndexStampStore _stampStore;
    private readonly ILogger<DualCollectionHolder> _logger;
    private readonly object _gate = new();
    private readonly ZVecInFlightGate _inFlight = new();
    private string _modelId = SigLipModelCatalog.DefaultModelId;
    private int _embeddingDim = 768;
    private object _textCollection = null!;
    private object _imageCollection = null!;
    private bool _indexesEnsured;
    private bool _disposed;

    public DualCollectionHolder(
        IZvecFactory factory,
        IOptions<ProductSearchOptions> options,
        IIndexStampStore stampStore,
        ILogger<DualCollectionHolder> logger)
    {
        _factory = factory;
        _options = options.Value;
        _stampStore = stampStore;
        _logger = logger;
        var initial = SigLipModelCatalog.Get(
            string.IsNullOrWhiteSpace(_options.ActiveModelId)
                ? SigLipModelCatalog.DefaultModelId
                : _options.ActiveModelId);
        _modelId = initial.Id;
        _embeddingDim = initial.EmbeddingDim;
        (_textCollection, _imageCollection) = OpenBoth(initial);
    }

    public string ModelId
    {
        get { lock (_gate) return _modelId; }
    }

    public int EmbeddingDim
    {
        get { lock (_gate) return _embeddingDim; }
    }

    public string TextCollectionPath
    {
        get { lock (_gate) return _options.TextCollectionPathFor(_modelId); }
    }

    public string ImageCollectionPath
    {
        get { lock (_gate) return _options.ImageCollectionPathFor(_modelId); }
    }

    public (long TextCount, long ImageCount) DocCounts
    {
        get
        {
            _inFlight.Enter();
            try
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return ReadDocCountsUnlocked();
                }
            }
            finally
            {
                _inFlight.Leave();
            }
        }
    }

    public void SwitchToModel(SigLipModelDefinition model)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (string.Equals(_modelId, model.Id, StringComparison.OrdinalIgnoreCase)
                && _embeddingDim == model.EmbeddingDim)
                return;

            _inFlight.Drain();
            DisposeBothUnlocked();
            _modelId = model.Id;
            _embeddingDim = model.EmbeddingDim;
            _indexesEnsured = false;
            (_textCollection, _imageCollection) = OpenBoth(model);
        }
    }

    public void EnsureIndexes()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_indexesEnsured)
                return;

            try
            {
                CreateIndexesUnlocked();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ZVec index creation failed — recreating collections with current schema");
                _inFlight.Drain();
                RecreateEmptyUnlocked();
                CreateIndexesUnlocked();
            }

            _indexesEnsured = true;
        }
    }

    public async Task UpsertTextBatchAsync(
        IReadOnlyList<(string Id, string ConcatenatedText, string Gender, string BaseColour, string Season, string Usage, string MasterCategory, float[] Embedding)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0)
            return;

        _inFlight.Enter();
        try
        {
            if (_embeddingDim == 1152)
            {
                IZvecCollection<ProductTextDoc1152> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductTextDoc1152>)_textCollection;
                }

                await UpsertParallelAsync(batch, (item, token) => col.UpsertAsync(new ProductTextDoc1152
                {
                    Id = item.Id,
                    ConcatenatedText = item.ConcatenatedText,
                    Gender = item.Gender,
                    BaseColour = item.BaseColour,
                    Season = item.Season,
                    Usage = item.Usage,
                    MasterCategory = item.MasterCategory,
                    TextEmbedding = item.Embedding
                }, token), ct).ConfigureAwait(false);
                return;
            }

            {
                IZvecCollection<ProductTextDoc768> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductTextDoc768>)_textCollection;
                }

                await UpsertParallelAsync(batch, (item, token) => col.UpsertAsync(new ProductTextDoc768
                {
                    Id = item.Id,
                    ConcatenatedText = item.ConcatenatedText,
                    Gender = item.Gender,
                    BaseColour = item.BaseColour,
                    Season = item.Season,
                    Usage = item.Usage,
                    MasterCategory = item.MasterCategory,
                    TextEmbedding = item.Embedding
                }, token), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _inFlight.Leave();
        }
    }

    public async Task UpsertImageBatchAsync(
        IReadOnlyList<(string Id, float[] Embedding)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0)
            return;

        _inFlight.Enter();
        try
        {
            if (_embeddingDim == 1152)
            {
                IZvecCollection<ProductImageDoc1152> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductImageDoc1152>)_imageCollection;
                }

                await UpsertParallelAsync(batch, (item, token) => col.UpsertAsync(new ProductImageDoc1152
                {
                    Id = item.Id,
                    ImageEmbedding = item.Embedding
                }, token), ct).ConfigureAwait(false);
                return;
            }

            {
                IZvecCollection<ProductImageDoc768> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductImageDoc768>)_imageCollection;
                }

                await UpsertParallelAsync(batch, (item, token) => col.UpsertAsync(new ProductImageDoc768
                {
                    Id = item.Id,
                    ImageEmbedding = item.Embedding
                }, token), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _inFlight.Leave();
        }
    }

    private Task UpsertParallelAsync<T>(
        IReadOnlyList<T> batch,
        Func<T, CancellationToken, ValueTask<ZVecStatus>> upsert,
        CancellationToken ct)
    {
        var parallelism = Math.Max(1, _options.IngestZVecParallelism);
        return Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, token) => await upsert(item, token).ConfigureAwait(false));
    }

    public async Task DeleteByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
            return;

        _inFlight.Enter();
        try
        {
            if (_embeddingDim == 1152)
            {
                IZvecCollection<ProductTextDoc1152> text;
                IZvecCollection<ProductImageDoc1152> image;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    text = (IZvecCollection<ProductTextDoc1152>)_textCollection;
                    image = (IZvecCollection<ProductImageDoc1152>)_imageCollection;
                }

                foreach (var id in list)
                {
                    ct.ThrowIfCancellationRequested();
                    text.Delete(id);
                    image.Delete(id);
                }
                return;
            }

            {
                IZvecCollection<ProductTextDoc768> text;
                IZvecCollection<ProductImageDoc768> image;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    text = (IZvecCollection<ProductTextDoc768>)_textCollection;
                    image = (IZvecCollection<ProductImageDoc768>)_imageCollection;
                }

                foreach (var id in list)
                {
                    ct.ThrowIfCancellationRequested();
                    text.Delete(id);
                    image.Delete(id);
                }
            }
        }
        finally
        {
            _inFlight.Leave();
        }

        await Task.CompletedTask;
    }

    public void OptimizeBoth()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_embeddingDim == 1152)
            {
                ((IZvecCollection<ProductTextDoc1152>)_textCollection).Optimize();
                ((IZvecCollection<ProductImageDoc1152>)_imageCollection).Optimize();
            }
            else
            {
                ((IZvecCollection<ProductTextDoc768>)_textCollection).Optimize();
                ((IZvecCollection<ProductImageDoc768>)_imageCollection).Optimize();
            }

            // Stale handle after Optimize — SDK Dispose (close) then reopen same path.
            _inFlight.Drain();
            var model = SigLipModelCatalog.Get(_modelId);
            DisposeBothUnlocked();
            (_textCollection, _imageCollection) = ReopenBothWithBackoff(model);
        }
    }

    public void ReopenBoth()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _inFlight.Drain();
            var model = SigLipModelCatalog.Get(_modelId);
            DisposeBothUnlocked();
            (_textCollection, _imageCollection) = ReopenBothWithBackoff(model);
        }
    }

    public void RecreateEmpty()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _inFlight.Drain();
            RecreateEmptyUnlocked();
        }
    }

    public async Task<IReadOnlyList<(string Id, float Score)>> QueryTextDenseAsync(
        float[] vector,
        int topK,
        string? filter = null,
        CancellationToken ct = default)
    {
        if (filter is not null)
        {
            return await QueryTextUntypedAsync(
                [new ZVecQuery { FieldName = "TextEmbedding", Vector = vector }],
                filter,
                reranker: null,
                topK,
                ct).ConfigureAwait(false);
        }

        _inFlight.Enter();
        try
        {
            if (_embeddingDim == 1152)
            {
                IZvecCollection<ProductTextDoc1152> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductTextDoc1152>)_textCollection;
                }

                var hits = await col.QueryAsync(p => p.TextEmbedding, vector, topK, filter: null, includeVector: false, ct)
                    .ConfigureAwait(false);
                return hits.Select(h => (h.Record.Id, h.Score)).ToList();
            }

            {
                IZvecCollection<ProductTextDoc768> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductTextDoc768>)_textCollection;
                }

                var hits = await col.QueryAsync(p => p.TextEmbedding, vector, topK, filter: null, includeVector: false, ct)
                    .ConfigureAwait(false);
                return hits.Select(h => (h.Record.Id, h.Score)).ToList();
            }
        }
        finally
        {
            _inFlight.Leave();
        }
    }

    public async Task<IReadOnlyList<(string Id, float Score)>> QueryImageDenseAsync(
        float[] vector,
        int topK,
        string? filter = null,
        CancellationToken ct = default)
    {
        var fetchK = filter is null ? topK : Math.Min(topK * 8, Math.Max(topK, 80));

        _inFlight.Enter();
        try
        {
            if (_embeddingDim == 1152)
            {
                IZvecCollection<ProductImageDoc1152> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductImageDoc1152>)_imageCollection;
                }

                var hits = await col.QueryAsync(p => p.ImageEmbedding, vector, fetchK, filter: null, includeVector: false, ct)
                    .ConfigureAwait(false);
                return hits.Select(h => (h.Record.Id, h.Score)).Take(topK).ToList();
            }

            {
                IZvecCollection<ProductImageDoc768> col;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    col = (IZvecCollection<ProductImageDoc768>)_imageCollection;
                }

                var hits = await col.QueryAsync(p => p.ImageEmbedding, vector, fetchK, filter: null, includeVector: false, ct)
                    .ConfigureAwait(false);
                return hits.Select(h => (h.Record.Id, h.Score)).Take(topK).ToList();
            }
        }
        finally
        {
            _inFlight.Leave();
        }
    }

    public async Task<IReadOnlyList<(string Id, float Score)>> QueryTextUntypedAsync(
        IReadOnlyList<ZVecQuery> queries,
        string? filter,
        ZVecReranker? reranker,
        int topK,
        CancellationToken ct = default)
    {
        _inFlight.Enter();
        try
        {
            IZvecCollection col;
            lock (_gate)
            {
                ThrowIfDisposed();
                col = GetTextCollectionUntyped();
            }

            var docs = await Task.Run(() =>
            {
                if (queries.Count == 1)
                    return col.Query(queries[0], topk: topK, filter: filter, includeVector: false);

                return col.Query(queries, topk: topK, reranker: reranker!, filter: filter, includeVector: false);
            }, ct).ConfigureAwait(false);

            return docs.Select(d => (d.Id, d.Score)).ToList();
        }
        finally
        {
            _inFlight.Leave();
        }
    }

    private void CreateIndexesUnlocked()
    {
        if (_embeddingDim == 1152)
        {
            var text = (IZvecCollection<ProductTextDoc1152>)_textCollection;
            text.CreateIndex(p => p.Gender, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.BaseColour, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.Season, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.Usage, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.MasterCategory, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.ConcatenatedText, new ZVecFtsIndexParam
            {
                Tokenizer = ZVecFtsTokenizer.Standard,
                Filters = [ZVecFtsTokenFilter.Lowercase, ZVecFtsTokenFilter.AsciiFolding]
            });
        }
        else
        {
            var text = (IZvecCollection<ProductTextDoc768>)_textCollection;
            text.CreateIndex(p => p.Gender, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.BaseColour, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.Season, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.Usage, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.MasterCategory, new ZVecInvertIndexParam());
            text.CreateIndex(p => p.ConcatenatedText, new ZVecFtsIndexParam
            {
                Tokenizer = ZVecFtsTokenizer.Standard,
                Filters = [ZVecFtsTokenFilter.Lowercase, ZVecFtsTokenFilter.AsciiFolding]
            });
        }
    }

    private (long TextCount, long ImageCount) ReadDocCountsUnlocked()
    {
        if (_embeddingDim == 1152)
        {
            var text = (IZvecCollection<ProductTextDoc1152>)_textCollection;
            var image = (IZvecCollection<ProductImageDoc1152>)_imageCollection;
            return (text.Stats.DocCount, image.Stats.DocCount);
        }

        var text768 = (IZvecCollection<ProductTextDoc768>)_textCollection;
        var image768 = (IZvecCollection<ProductImageDoc768>)_imageCollection;
        return (text768.Stats.DocCount, image768.Stats.DocCount);
    }

    private void RecreateEmptyUnlocked()
    {
        var model = SigLipModelCatalog.Get(_modelId);
        DestroyBothUnlocked();
        (_textCollection, _imageCollection) = OpenBoth(model);
        _indexesEnsured = false;
    }

    private IZvecCollection GetTextCollectionUntyped()
        => ((dynamic)_textCollection).Untyped;

    private (object Text, object Image) OpenBoth(SigLipModelDefinition model)
    {
        var wipedAny = false;
        var textPath = _options.TextCollectionPathFor(model.Id);
        var imagePath = _options.ImageCollectionPathFor(model.Id);

        object text;
        object image;
        if (model.EmbeddingDim == 1152)
        {
            text = OpenSingle<ProductTextDoc1152>(textPath, ref wipedAny);
            image = OpenSingle<ProductImageDoc1152>(imagePath, ref wipedAny);
        }
        else
        {
            text = OpenSingle<ProductTextDoc768>(textPath, ref wipedAny);
            image = OpenSingle<ProductImageDoc768>(imagePath, ref wipedAny);
        }

        if (wipedAny)
            ResetStampAfterCorruptRecovery(model);

        return (text, image);
    }

    private (object Text, object Image) ReopenBothWithBackoff(SigLipModelDefinition model)
    {
        var textPath = _options.TextCollectionPathFor(model.Id);
        var imagePath = _options.ImageCollectionPathFor(model.Id);

        object text;
        object image;
        if (model.EmbeddingDim == 1152)
        {
            text = ReopenSingleWithBackoff<ProductTextDoc1152>(textPath);
            image = ReopenSingleWithBackoff<ProductImageDoc1152>(imagePath);
        }
        else
        {
            text = ReopenSingleWithBackoff<ProductTextDoc768>(textPath);
            image = ReopenSingleWithBackoff<ProductImageDoc768>(imagePath);
        }

        return (text, image);
    }

    private object OpenSingle<T>(string path, ref bool wipedAny)
        where T : class, new()
    {
        var localWiped = false;
        var collection = ZVecCollectionOpenHelper.OpenOrCreateWithRecovery<T>(
            _factory, path, _options.EnableMmap, _logger, ref localWiped);
        if (localWiped)
            wipedAny = true;
        return collection;
    }

    private object ReopenSingleWithBackoff<T>(string path)
        where T : class, new()
    {
        var wiped = false;
        Exception? last = null;

        foreach (var delayMs in ReopenBackoffMs)
        {
            try
            {
                return ZVecCollectionOpenHelper.OpenOrCreateWithRecovery<T>(
                    _factory, path, _options.EnableMmap, _logger, ref wiped);
            }
            catch (Exception ex) when (ZVecCollectionOpenHelper.IsLockOpenFailure(ex))
            {
                last = ex;
                Thread.Sleep(delayMs);
            }
        }

        try
        {
            return ZVecCollectionOpenHelper.OpenOrCreateWithRecovery<T>(
                _factory, path, _options.EnableMmap, _logger, ref wiped);
        }
        catch (Exception ex) when (last is not null && ZVecCollectionOpenHelper.IsLockOpenFailure(ex))
        {
            throw new InvalidOperationException(
                $"ZVec could not reopen collection at '{path}' after close backoff. " +
                "Another ProductSearch.Api instance may still be running.",
                ex);
        }
    }

    private void ResetStampAfterCorruptRecovery(SigLipModelDefinition model)
    {
        _logger.LogWarning(
            "ZVec on-disk store was corrupt and recreated empty for {ModelId} — resetting ingest stamp to 0",
            model.Id);

        var stamp = _stampStore.Load();
        _stampStore.Save(new IndexStamp(
            model.Id,
            model.EmbeddingDim,
            SigLipModelCatalog.EncodePipelineVersion,
            0));

        if (stamp.IngestOffset > 0)
        {
            _logger.LogWarning(
                "Previous ingest offset was {Offset} — re-ingest required to rebuild vectors",
                stamp.IngestOffset);
        }
    }

    private void DestroyBothUnlocked()
    {
        try
        {
            if (_embeddingDim == 1152)
            {
                ((IZvecCollection<ProductTextDoc1152>)_textCollection).Destroy();
                ((IZvecCollection<ProductImageDoc1152>)_imageCollection).Destroy();
            }
            else
            {
                ((IZvecCollection<ProductTextDoc768>)_textCollection).Destroy();
                ((IZvecCollection<ProductImageDoc768>)_imageCollection).Destroy();
            }
        }
        catch (ObjectDisposedException)
        {
            // Already closed — fall through to directory cleanup if needed.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ZVec Destroy failed — falling back to directory delete");
            ZVecCollectionOpenHelper.TryDeleteDir(TextCollectionPath);
            ZVecCollectionOpenHelper.TryDeleteDir(ImageCollectionPath);
        }
    }

    private void DisposeBothUnlocked()
    {
        DisposeCollection(_textCollection, "text");
        DisposeCollection(_imageCollection, "image");
    }

    private void DisposeCollection(object collection, string label)
    {
        if (collection is IAsyncDisposable asyncDisposable)
        {
            try { asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose {Label} ZVec collection", label); }
            return;
        }

        if (collection is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose {Label} ZVec collection", label); }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DualCollectionHolder));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _inFlight.Drain(TimeSpan.FromSeconds(10));
            DisposeBothUnlocked();
            _disposed = true;
        }
    }
}
