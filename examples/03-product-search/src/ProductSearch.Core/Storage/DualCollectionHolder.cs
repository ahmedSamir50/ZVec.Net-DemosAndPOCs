using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZVec.NET;
using ZVec.NET.Query;

namespace ProductSearch.Core.Storage;

public sealed class DualCollectionHolder : IDisposable
{
    private readonly IZvecFactory _factory;
    private readonly ProductSearchOptions _options;
    private readonly ILogger<DualCollectionHolder> _logger;
    private readonly object _gate = new();
    private string _modelId = SigLipModelCatalog.DefaultModelId;
    private int _embeddingDim = 768;
    private object _textCollection = null!;
    private object _imageCollection = null!;
    private bool _indexesEnsured;

    public DualCollectionHolder(IZvecFactory factory, IOptions<ProductSearchOptions> options, ILogger<DualCollectionHolder> logger)
    {
        _factory = factory;
        _options = options.Value;
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

    public string TextCollectionPath => _options.TextCollectionPathFor(_modelId);
    public string ImageCollectionPath => _options.ImageCollectionPathFor(_modelId);

    public (long TextCount, long ImageCount) DocCounts
    {
        get
        {
            lock (_gate)
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
        }
    }

    public void SwitchToModel(SigLipModelDefinition model)
    {
        lock (_gate)
        {
            if (string.Equals(_modelId, model.Id, StringComparison.OrdinalIgnoreCase)
                && _embeddingDim == model.EmbeddingDim)
                return;

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
            if (_indexesEnsured)
                return;

            try
            {
                CreateIndexesUnlocked();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ZVec index creation failed — recreating collections with current schema");
                RecreateEmptyUnlocked();
                CreateIndexesUnlocked();
            }

            _indexesEnsured = true;
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

    public async Task UpsertTextBatchAsync(
        IReadOnlyList<(string Id, string ConcatenatedText, string Gender, string BaseColour, string Season, string Usage, string MasterCategory, float[] Embedding)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0)
            return;

        if (_embeddingDim == 1152)
        {
            IZvecCollection<ProductTextDoc1152> col;
            lock (_gate) col = (IZvecCollection<ProductTextDoc1152>)_textCollection;
            foreach (var item in batch)
            {
                await col.UpsertAsync(new ProductTextDoc1152
                {
                    Id = item.Id,
                    ConcatenatedText = item.ConcatenatedText,
                    Gender = item.Gender,
                    BaseColour = item.BaseColour,
                    Season = item.Season,
                    Usage = item.Usage,
                    MasterCategory = item.MasterCategory,
                    TextEmbedding = item.Embedding
                }, ct).ConfigureAwait(false);
            }
            return;
        }

        {
            IZvecCollection<ProductTextDoc768> col;
            lock (_gate) col = (IZvecCollection<ProductTextDoc768>)_textCollection;
            foreach (var item in batch)
            {
                await col.UpsertAsync(new ProductTextDoc768
                {
                    Id = item.Id,
                    ConcatenatedText = item.ConcatenatedText,
                    Gender = item.Gender,
                    BaseColour = item.BaseColour,
                    Season = item.Season,
                    Usage = item.Usage,
                    MasterCategory = item.MasterCategory,
                    TextEmbedding = item.Embedding
                }, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task UpsertImageBatchAsync(
        IReadOnlyList<(string Id, float[] Embedding)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0)
            return;

        if (_embeddingDim == 1152)
        {
            IZvecCollection<ProductImageDoc1152> col;
            lock (_gate) col = (IZvecCollection<ProductImageDoc1152>)_imageCollection;
            foreach (var item in batch)
            {
                await col.UpsertAsync(new ProductImageDoc1152
                {
                    Id = item.Id,
                    ImageEmbedding = item.Embedding
                }, ct).ConfigureAwait(false);
            }
            return;
        }

        {
            IZvecCollection<ProductImageDoc768> col;
            lock (_gate) col = (IZvecCollection<ProductImageDoc768>)_imageCollection;
            foreach (var item in batch)
            {
                await col.UpsertAsync(new ProductImageDoc768
                {
                    Id = item.Id,
                    ImageEmbedding = item.Embedding
                }, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
            return;

        if (_embeddingDim == 1152)
        {
            IZvecCollection<ProductTextDoc1152> text;
            IZvecCollection<ProductImageDoc1152> image;
            lock (_gate)
            {
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

        await Task.CompletedTask;
    }

    public void OptimizeBoth()
    {
        lock (_gate)
        {
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

            var model = SigLipModelCatalog.Get(_modelId);
            DisposeBothUnlocked();
            (_textCollection, _imageCollection) = OpenBoth(model);
        }
    }

    public void ReopenBoth()
    {
        lock (_gate)
        {
            var model = SigLipModelCatalog.Get(_modelId);
            DisposeBothUnlocked();
            (_textCollection, _imageCollection) = OpenBoth(model);
        }
    }

    public void RecreateEmpty()
    {
        lock (_gate)
        {
            RecreateEmptyUnlocked();
        }
    }

    private void RecreateEmptyUnlocked()
    {
        var model = SigLipModelCatalog.Get(_modelId);
        TryDestroyBoth();
        (_textCollection, _imageCollection) = OpenBoth(model);
        _indexesEnsured = false;
    }

    public IZvecCollection GetTextCollectionUntyped()
    {
        lock (_gate) return ((dynamic)_textCollection).Untyped;
    }

    public IZvecCollection GetImageCollectionUntyped()
    {
        lock (_gate) return ((dynamic)_imageCollection).Untyped;
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

        if (_embeddingDim == 1152)
        {
            IZvecCollection<ProductTextDoc1152> col;
            lock (_gate) col = (IZvecCollection<ProductTextDoc1152>)_textCollection;
            var hits = await col.QueryAsync(p => p.TextEmbedding, vector, topK, filter: null, includeVector: false, ct)
                .ConfigureAwait(false);
            return hits.Select(h => (h.Record.Id, h.Score)).ToList();
        }

        {
            IZvecCollection<ProductTextDoc768> col;
            lock (_gate) col = (IZvecCollection<ProductTextDoc768>)_textCollection;
            var hits = await col.QueryAsync(p => p.TextEmbedding, vector, topK, filter: null, includeVector: false, ct)
                .ConfigureAwait(false);
            return hits.Select(h => (h.Record.Id, h.Score)).ToList();
        }
    }

    public async Task<IReadOnlyList<(string Id, float Score)>> QueryImageDenseAsync(
        float[] vector,
        int topK,
        string? filter = null,
        CancellationToken ct = default)
    {
        var fetchK = filter is null ? topK : Math.Min(topK * 8, Math.Max(topK, 80));
        if (_embeddingDim == 1152)
        {
            IZvecCollection<ProductImageDoc1152> col;
            lock (_gate) col = (IZvecCollection<ProductImageDoc1152>)_imageCollection;
            var hits = await col.QueryAsync(p => p.ImageEmbedding, vector, fetchK, filter: null, includeVector: false, ct)
                .ConfigureAwait(false);
            return hits.Select(h => (h.Record.Id, h.Score)).Take(topK).ToList();
        }

        {
            IZvecCollection<ProductImageDoc768> col;
            lock (_gate) col = (IZvecCollection<ProductImageDoc768>)_imageCollection;
            var hits = await col.QueryAsync(p => p.ImageEmbedding, vector, fetchK, filter: null, includeVector: false, ct)
                .ConfigureAwait(false);
            return hits.Select(h => (h.Record.Id, h.Score)).Take(topK).ToList();
        }
    }

    public async Task<IReadOnlyList<(string Id, float Score)>> QueryTextUntypedAsync(
        IReadOnlyList<ZVecQuery> queries,
        string? filter,
        ZVecReranker? reranker,
        int topK,
        CancellationToken ct = default)
    {
        IZvecCollection col;
        lock (_gate) col = GetTextCollectionUntyped();

        var docs = await Task.Run(() =>
        {
            if (queries.Count == 1)
                return col.Query(queries[0], topk: topK, filter: filter, includeVector: false);

            return col.Query(queries, topk: topK, reranker: reranker!, filter: filter, includeVector: false);
        }, ct).ConfigureAwait(false);

        return docs.Select(d => (d.Id, d.Score)).ToList();
    }

    private (object Text, object Image) OpenBoth(SigLipModelDefinition model)
    {
        var textPath = _options.TextCollectionPathFor(model.Id);
        var imagePath = _options.ImageCollectionPathFor(model.Id);
        if (model.EmbeddingDim == 1152)
        {
            return (
                CollectionBootstrap.OpenOrCreate<ProductTextDoc1152>(_factory, textPath, _options.EnableMmap),
                CollectionBootstrap.OpenOrCreate<ProductImageDoc1152>(_factory, imagePath, _options.EnableMmap));
        }

        return (
            CollectionBootstrap.OpenOrCreate<ProductTextDoc768>(_factory, textPath, _options.EnableMmap),
            CollectionBootstrap.OpenOrCreate<ProductImageDoc768>(_factory, imagePath, _options.EnableMmap));
    }

    private void TryDestroyBoth()
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
        catch
        {
            TryDeleteDir(TextCollectionPath);
            TryDeleteDir(ImageCollectionPath);
        }
    }

    private static void TryDeleteDir(string path)
    {
        if (!Directory.Exists(path))
            return;
        try { Directory.Delete(path, recursive: true); }
        catch { Thread.Sleep(150); try { Directory.Delete(path, recursive: true); } catch { /* ignore */ } }
    }

    private void DisposeBothUnlocked()
    {
        try { (_textCollection as IDisposable)?.Dispose(); } catch { /* ignore */ }
        try { (_imageCollection as IDisposable)?.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        lock (_gate) DisposeBothUnlocked();
    }
}
