using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;
using ZVec.NET;

namespace ClipOnnx.App.Storage;

/// <summary>
/// One ANN hit from the active gallery.
/// <see cref="Score"/> is ZVec Cosine <b>distance</b> (lower = better); convert before UI %.
/// </summary>
public sealed record GalleryQueryHit(string Id, string Path, float Score);

/// <summary>
/// Holds the live gallery collection for the <b>active</b> CLIP model.
/// Typed ZVec fixes vector dim at compile time, so we use two POCOs
/// (<see cref="ImageAsset512"/> / <see cref="ImageAsset768"/>) and a separate
/// on-disk path per model id: <c>{CollectionPath}/{modelId}</c>.
///
/// Switching models calls <see cref="SwitchToModel"/>; searching after a model change
/// without Reset+Ingest is blocked by the gallery stamp (different embedding spaces).
/// </summary>
public sealed class GalleryStore : IDisposable
{
    private readonly IZvecFactory _factory;
    private readonly ClipOnnxOptions _options;
    private readonly object _gate = new();
    private string _modelId;
    private int _embeddingDim;
    private object _collection; // IZvecCollection<ImageAsset512> | IZvecCollection<ImageAsset768>

    public GalleryStore(IZvecFactory factory, IOptions<ClipOnnxOptions> options)
    {
        _factory = factory;
        _options = options.Value;
        var initial = ClipModelCatalog.Get(
            string.IsNullOrWhiteSpace(_options.ActiveModelId)
                ? ClipModelCatalog.DefaultModelId
                : _options.ActiveModelId);
        _modelId = initial.Id;
        _embeddingDim = initial.EmbeddingDim;
        _collection = OpenCollection(initial);
    }

    public string ModelId
    {
        get { lock (_gate) return _modelId; }
    }

    public int EmbeddingDim
    {
        get { lock (_gate) return _embeddingDim; }
    }

    public string CollectionPath
    {
        get
        {
            lock (_gate)
                return PathFor(_modelId);
        }
    }

    public string PathFor(string modelId)
        => Path.GetFullPath(Path.Combine(_options.CollectionPath, modelId));

    /// <summary>Switch on-disk collection to match the active CLIP model (call after model select / reset).</summary>
    public void SwitchToModel(ClipModelDefinition model)
    {
        lock (_gate)
        {
            if (string.Equals(_modelId, model.Id, StringComparison.OrdinalIgnoreCase)
                && _embeddingDim == model.EmbeddingDim)
                return;

            DisposeCollectionUnlocked();
            _modelId = model.Id;
            _embeddingDim = model.EmbeddingDim;
            _collection = OpenCollection(model);
        }
    }

    public bool Exists(string id)
    {
        lock (_gate)
        {
            return _embeddingDim == 768
                ? ((IZvecCollection<ImageAsset768>)_collection).Fetch(id) is not null
                : ((IZvecCollection<ImageAsset512>)_collection).Fetch(id) is not null;
        }
    }

    public async Task UpsertAsync(string id, string path, float[] embedding, CancellationToken ct = default)
    {
        if (embedding.Length != EmbeddingDim)
            throw new ArgumentException($"Embedding length {embedding.Length} != gallery dim {EmbeddingDim}.");

        if (_embeddingDim == 768)
        {
            IZvecCollection<ImageAsset768> col;
            lock (_gate) col = (IZvecCollection<ImageAsset768>)_collection;
            await col.UpsertAsync(new ImageAsset768 { Id = id, Path = path, Embedding = embedding }, ct);
        }
        else
        {
            IZvecCollection<ImageAsset512> col;
            lock (_gate) col = (IZvecCollection<ImageAsset512>)_collection;
            await col.UpsertAsync(new ImageAsset512 { Id = id, Path = path, Embedding = embedding }, ct);
        }
    }

    public async Task<IReadOnlyList<GalleryQueryHit>> QueryAsync(float[] vector, int topK, CancellationToken ct = default)
    {
        if (vector.Length != EmbeddingDim)
            throw new ArgumentException($"Query vector length {vector.Length} != gallery dim {EmbeddingDim}.");

        if (_embeddingDim == 768)
        {
            IZvecCollection<ImageAsset768> col;
            lock (_gate) col = (IZvecCollection<ImageAsset768>)_collection;
            var hits = await col.QueryAsync(a => a.Embedding, vector, topK, filter: null, includeVector: false, ct);
            return hits.Select(h => new GalleryQueryHit(h.Record.Id, h.Record.Path, h.Score)).ToList();
        }
        else
        {
            IZvecCollection<ImageAsset512> col;
            lock (_gate) col = (IZvecCollection<ImageAsset512>)_collection;
            var hits = await col.QueryAsync(a => a.Embedding, vector, topK, filter: null, includeVector: false, ct);
            return hits.Select(h => new GalleryQueryHit(h.Record.Id, h.Record.Path, h.Score)).ToList();
        }
    }

    /// <summary>Destroy on-disk collection for the active model and open a fresh empty one.</summary>
    public void RecreateEmpty()
    {
        lock (_gate)
        {
            var model = ClipModelCatalog.Get(_modelId);
            var path = PathFor(_modelId);
            try
            {
                if (_embeddingDim == 768)
                    ((IZvecCollection<ImageAsset768>)_collection).Destroy();
                else
                    ((IZvecCollection<ImageAsset512>)_collection).Destroy();
            }
            catch (ObjectDisposedException) { /* already closed */ }
            catch
            {
                try { (_collection as IDisposable)?.Dispose(); } catch { /* ignore */ }
                if (Directory.Exists(path))
                {
                    Thread.Sleep(150);
                    Directory.Delete(path, recursive: true);
                }
            }

            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                Thread.Sleep(150);
                try { Directory.Delete(path, recursive: true); } catch { /* Destroy may have removed it */ }
            }

            _collection = OpenCollection(model);
        }
    }

    private object OpenCollection(ClipModelDefinition model)
    {
        var path = PathFor(model.Id);
        if (model.EmbeddingDim == 768)
            return CollectionBootstrap.OpenOrCreate<ImageAsset768>(_factory, path, _options.EnableMmap);
        return CollectionBootstrap.OpenOrCreate<ImageAsset512>(_factory, path, _options.EnableMmap);
    }

    private void DisposeCollectionUnlocked()
    {
        try { (_collection as IDisposable)?.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        lock (_gate) DisposeCollectionUnlocked();
    }
}
