using MovieRecs.Maui.Encoding;
using MovieRecs.Maui.Models;
using MovieRecs.Maui.Options;
using ZVec.NET;

namespace MovieRecs.Maui.Services;

public interface IRecommendService
{
    Task EnsureReadyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RecommendHit>> RecommendAsync(
        IReadOnlyList<string> movieIds,
        int topK,
        string? genreFilter = null,
        CancellationToken ct = default);
}

/// <summary>
/// Netflix-style recs: mean of liked movie embeddings → ZVec <c>QueryAsync</c> → drop seen.
/// </summary>
public sealed class RecommendService : IRecommendService
{
    private readonly IMiniLmEncoder _encoder;
    private readonly IMovieLensCatalog _catalog;
    private readonly IMovieStore _store;
    private readonly IIndexStampStore _stamp;
    private readonly MovieRecsOptions _options;

    public RecommendService(
        IMiniLmEncoder encoder,
        IMovieLensCatalog catalog,
        IMovieStore store,
        IIndexStampStore stamp,
        MovieRecsOptions options)
    {
        _encoder = encoder;
        _catalog = catalog;
        _store = store;
        _stamp = stamp;
        _options = options;
    }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        await _catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);
        await _encoder.EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (_store.Collection is null)
            _store.Open();
    }

    public async Task<IReadOnlyList<RecommendHit>> RecommendAsync(
        IReadOnlyList<string> movieIds,
        int topK,
        string? genreFilter = null,
        CancellationToken ct = default)
    {
        if (movieIds.Count == 0)
            throw new ArgumentException("Select at least one movie for the behaviour vector.", nameof(movieIds));
        if (!_stamp.IsReady())
            throw new InvalidOperationException("Index is not ready. Run Ingest first.");

        await EnsureReadyAsync(ct).ConfigureAwait(false);

        // Re-embed watchlist text at query time (same EmbedText as ingest) so we do not need
        // to Fetch vectors from ZVec for the mean-pool. Seen ids are excluded from results.
        var vectors = new List<float[]>(movieIds.Count);
        var seen = new HashSet<string>(movieIds, StringComparer.Ordinal);
        foreach (var id in movieIds)
        {
            if (!_catalog.ById.TryGetValue(id, out var info))
                continue;
            vectors.Add(_encoder.Embed(info.EmbedText));
        }

        if (vectors.Count == 0)
            throw new InvalidOperationException("None of the selected movie ids were found in the catalog.");

        var userVec = VectorMath.AverageThenL2Normalize(vectors);
        var genre = string.IsNullOrWhiteSpace(genreFilter) ? null : genreFilter.Trim();
        // Over-fetch so we can drop seen titles and optional genre matches in-process
        // (genre filter is post-query for demo simplicity — not a typed ZVec Invert filter).
        var fetch = Math.Clamp(topK + seen.Count + (genre is null ? 20 : 80), topK, 300);

        IReadOnlyList<ZVecHit<Movie>> hits;
        try
        {
            hits = await QueryHitsAsync(userVec, fetch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStaleQuerierError(ex))
        {
            // One reopen+retry — Optimize can leave a stale querier; corrupt disk needs Reset.
            _store.Open();
            try
            {
                hits = await QueryHitsAsync(userVec, fetch, ct).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (IsStaleQuerierError(retryEx))
            {
                throw new InvalidOperationException(
                    "ZVec query failed after reopen (likely a corrupt index). Use Reset index, then Ingest.",
                    retryEx);
            }
        }

        var results = new List<RecommendHit>(topK);
        foreach (var hit in hits)
        {
            var rec = hit.Record;
            if (rec is null || seen.Contains(rec.Id))
                continue;
            if (genre is not null
                && rec.Genres.IndexOf(genre, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var (cosine, pct) = VectorMath.FromZVecDistance(hit.Score);
            results.Add(new RecommendHit(rec.Id, rec.Title, rec.Genres, rec.Year, cosine, pct));
            if (results.Count >= topK)
                break;
        }

        return results.OrderByDescending(r => r.Cosine).ToList();
    }

    private async Task<IReadOnlyList<ZVecHit<Movie>>> QueryHitsAsync(
        float[] userVec,
        int fetch,
        CancellationToken ct)
    {
        var collection = _store.Collection
            ?? throw new InvalidOperationException("Collection is not open.");
        return await collection.QueryAsync(
            m => m.Embedding,
            userVec,
            fetch,
            filter: null,
            includeVector: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Native Query hydrate failures after Optimize / bad segments
    /// (Gandiva fill_result / fetch table / InternalError Query).
    /// </summary>
    private static bool IsStaleQuerierError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("fill_result", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("fetch table", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("InternalError (Query)", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Gandiva", StringComparison.OrdinalIgnoreCase))
            return true;

        return ex.InnerException is not null && IsStaleQuerierError(ex.InnerException);
    }
}
