using System.Text.Json;
using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.Constants;
using PDDM.Core.Helpers;
using PDDM.Core.Models;
using PDDM.Core.Storage;
using ZVec.NET;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class VectorStoreService : IVectorStore
{
    private readonly DocsCollectionHolder _holder;
    private readonly PddmRuntimeSettings _runtimeSettings;

    /// <summary>Creates the vector store facade.</summary>
    public VectorStoreService(DocsCollectionHolder holder, PddmRuntimeSettings runtimeSettings)
    {
        _holder = holder;
        _runtimeSettings = runtimeSettings;
    }

    private IZvecCollection<JiraDocChunk> Collection => _holder.Collection;

    /// <inheritdoc />
    public void InsertBatch(IReadOnlyList<JiraDocChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
            return;

        Collection.Insert(chunks);
    }

    /// <inheritdoc />
    public JiraDocChunk? FetchById(string id, bool includeVector = false)
        => Collection.Fetch(id, includeVector);

    /// <inheritdoc />
    public JiraDocChunk? FetchByKey(string key, bool includeVector = false)
    {
        foreach (var id in ChunkIdFormatter.PossibleIdsForKey(key))
        {
            var doc = Collection.Fetch(id, includeVector);
            if (doc is not null)
                return doc;
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ZVecHitAdapter> Query(
        ReadOnlyMemory<float> queryVector,
        int topK,
        Func<JiraDocChunk, bool>? clientFilter = null,
        bool includeVector = false,
        bool? containsDecision = null,
        int? tier = null,
        string? excludeKey = null)
    {
        System.Linq.Expressions.Expression<Func<JiraDocChunk, bool>>? filter = null;
        if (containsDecision == true && tier.HasValue && excludeKey is not null)
            filter = p => p.ContainsDecision == true && p.Tier == tier.Value && p.Key != excludeKey;
        else if (containsDecision == true && tier.HasValue)
            filter = p => p.ContainsDecision == true && p.Tier == tier.Value;
        else if (tier.HasValue && excludeKey is not null)
            filter = p => p.Tier == tier.Value && p.Key != excludeKey;
        else if (tier.HasValue)
            filter = p => p.Tier == tier.Value;
        else if (excludeKey is not null)
            filter = p => p.Key != excludeKey;

        var hits = Collection.Query(
            p => p.Embedding,
            queryVector,
            topK: topK,
            filter: filter,
            includeVector: includeVector);

        IEnumerable<ZVecHitAdapter> mapped = hits.Select(h => new ZVecHitAdapter
        {
            Record = h.Record,
            Score = h.Score
        });

        if (clientFilter is not null)
            mapped = mapped.Where(h => clientFilter(h.Record));

        return mapped.ToList();
    }

    /// <inheritdoc />
    public async Task SaveChunkIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var path = GetChunkIdsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(ids.Distinct().ToList());
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LoadChunkIdsAsync(CancellationToken cancellationToken = default)
    {
        var path = GetChunkIdsPath();
        if (!File.Exists(path))
            return [];

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    /// <inheritdoc />
    public void RecreateCollection()
    {
        var settings = _runtimeSettings.Current.ZVec;
        _holder.Recreate(settings.CollectionPath, settings.EnableMmap);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Inserts stage in a temporary flat buffer; Optimize merges into HNSW for production-quality ANN.
    /// </remarks>
    public void Optimize() => Collection.Optimize();

    private string GetChunkIdsPath()
    {
        var collectionPath = _runtimeSettings.Current.ZVec.CollectionPath;
        return Path.Combine(collectionPath, PddmDefaults.ChunkIdsFileName);
    }
}
