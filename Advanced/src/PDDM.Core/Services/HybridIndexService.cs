using System.Collections.Concurrent;
using PDDM.Core.Abstractions;
using PDDM.Core.Models;
using PDDM.Shared;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class HybridIndexService : IHybridIndex
{
    private readonly IVectorStore _vectorStore;
    private readonly ConcurrentDictionary<string, JiraDocChunk> _byId = new();
    private readonly ConcurrentDictionary<string, JiraDocChunk> _byJiraKey = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<JiraDocChunk>> _byEpicLink = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<JiraDocChunk>> _byParentKey = new();
    private readonly ConcurrentDictionary<int, ConcurrentBag<JiraDocChunk>> _byTier = new();

    /// <summary>Creates the hybrid navigation cache.</summary>
    public HybridIndexService(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <inheritdoc />
    public int TotalCount => _byId.Count;

    /// <inheritdoc />
    public void Add(JiraDocChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _byId[chunk.Id] = chunk;

        if (chunk.Tier <= (int)DocTier.SubTask)
            _byJiraKey[chunk.Key] = chunk;

        if (!string.IsNullOrEmpty(chunk.EpicLink))
        {
            _byEpicLink.AddOrUpdate(
                chunk.EpicLink,
                _ => new ConcurrentBag<JiraDocChunk> { chunk },
                (_, bag) => { bag.Add(chunk); return bag; });
        }

        if (!string.IsNullOrEmpty(chunk.ParentKey))
        {
            _byParentKey.AddOrUpdate(
                chunk.ParentKey,
                _ => new ConcurrentBag<JiraDocChunk> { chunk },
                (_, bag) => { bag.Add(chunk); return bag; });
        }

        _byTier.AddOrUpdate(
            chunk.Tier,
            _ => new ConcurrentBag<JiraDocChunk> { chunk },
            (_, bag) => { bag.Add(chunk); return bag; });
    }

    /// <inheritdoc />
    public void AddRange(IEnumerable<JiraDocChunk> chunks)
    {
        foreach (var chunk in chunks)
            Add(chunk);
    }

    /// <inheritdoc />
    public JiraDocChunk? GetById(string id)
        => _byId.TryGetValue(id, out var c) ? c : null;

    /// <inheritdoc />
    public JiraDocChunk? GetByKey(string jiraKey)
        => _byJiraKey.TryGetValue(jiraKey, out var c) ? c : null;

    /// <inheritdoc />
    public IReadOnlyList<JiraDocChunk> GetByEpicLink(string epicLink)
        => _byEpicLink.TryGetValue(epicLink, out var bag) ? bag.ToList() : [];

    /// <inheritdoc />
    public IReadOnlyList<JiraDocChunk> GetByParentKey(string parentKey)
        => _byParentKey.TryGetValue(parentKey, out var bag) ? bag.ToList() : [];

    /// <inheritdoc />
    public IReadOnlyList<JiraDocChunk> GetByTier(int tier)
        => _byTier.TryGetValue(tier, out var bag) ? bag.ToList() : [];

    /// <inheritdoc />
    public void Clear()
    {
        _byId.Clear();
        _byJiraKey.Clear();
        _byEpicLink.Clear();
        _byParentKey.Clear();
        _byTier.Clear();
    }

    /// <inheritdoc />
    public async Task RebuildFromStoreAsync(CancellationToken cancellationToken = default)
    {
        Clear();
        var ids = await _vectorStore.LoadChunkIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = _vectorStore.FetchById(id, includeVector: false);
            if (doc is not null)
                Add(doc);
        }
    }
}
