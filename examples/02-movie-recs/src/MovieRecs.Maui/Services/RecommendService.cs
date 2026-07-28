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
/// Netflix-style recs: mean of liked embeddings → ZVec ANN → genre + franchise rerank → gates.
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
            throw new InvalidOperationException("Index is not ready. Run Ingest first (or Reset if stamp mismatch).");

        await EnsureReadyAsync(ct).ConfigureAwait(false);

        var watch = new List<MovieInfo>(movieIds.Count);
        var vectors = new List<float[]>(movieIds.Count);
        var seen = new HashSet<string>(movieIds, StringComparer.Ordinal);
        foreach (var id in movieIds)
        {
            if (!_catalog.ById.TryGetValue(id, out var info))
                continue;
            watch.Add(info);
            vectors.Add(_encoder.Embed(info.EmbedText));
        }

        if (vectors.Count == 0)
            throw new InvalidOperationException("None of the selected movie ids were found in the catalog.");

        var userVec = VectorMath.AverageThenL2Normalize(vectors);
        var watchGenres = BuildGenreSet(watch);
        var watchTitles = watch.Select(m => m.Title).ToList();
        var genreFilterNorm = string.IsNullOrWhiteSpace(genreFilter) ? null : genreFilter.Trim();

        var fetch = Math.Clamp(_options.RecommendFetch, topK + seen.Count + 20, 300);
        IReadOnlyList<ZVecHit<Movie>> hits;
        try
        {
            hits = await QueryHitsAsync(userVec, fetch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStaleQuerierError(ex))
        {
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

        // Candidate map: id → (info, rawCosine from ANN or inject)
        var candidates = new Dictionary<string, (MovieInfo Info, double Cosine)>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            var rec = hit.Record;
            if (rec is null || seen.Contains(rec.Id))
                continue;
            if (genreFilterNorm is not null
                && rec.Genres.IndexOf(genreFilterNorm, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var (cosine, _) = VectorMath.FromZVecDistance(hit.Score);
            if (!_catalog.ById.TryGetValue(rec.Id, out var info))
                info = new MovieInfo(rec.Id, rec.Title, rec.Genres, rec.Year);
            candidates[rec.Id] = (info, cosine);
        }

        // Catalog inject: sequels/franchise mates ANN may have missed.
        InjectFranchiseMates(watch, seen, genreFilterNorm, userVec, candidates);

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No candidates after ANN + franchise inject. Reset index → Ingest if the stamp is stale.");
        }

        // Gates on raw cosine; sort on cosine + bonuses.
        var ranked = candidates.Values
            .Select(c =>
            {
                var genreBonus = GenreJaccard(watchGenres, c.Info.Genres) * _options.GenreJaccardBonusCap;
                var fran = watchTitles.Any(w => FranchiseTitle.SharesFranchise(w, c.Info.Title))
                    ? _options.FranchiseBonus
                    : 0.0;
                return (c.Info, c.Cosine, Score: c.Cosine + genreBonus + fran);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var topCosine = ranked.Max(x => x.Cosine);
        if (topCosine < _options.MinCosine)
        {
            throw new InvalidOperationException(
                $"No confident neighbors: best cosine {topCosine:F3} < min {_options.MinCosine:F3}. " +
                "Try another watchlist, or Reset → Ingest if the index is from an old encoder.");
        }

        var gap = Math.Max(0f, _options.MaxCosineGapFromTop);
        var results = new List<RecommendHit>(topK);
        foreach (var (info, cosine, _) in ranked)
        {
            if (cosine < _options.MinCosine)
                continue;
            if (cosine < topCosine - gap && !watchTitles.Any(w => FranchiseTitle.SharesFranchise(w, info.Title)))
                continue; // franchise mates may sit slightly below gap but still useful for demos

            var pct = (int)Math.Max(0, Math.Round(100.0 * cosine));
            results.Add(new RecommendHit(info.Id, info.Title, info.Genres, info.Year, cosine, pct));
            if (results.Count >= topK)
                break;
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                "Neighbors existed but none passed confidence gates. Clear genre filter or widen the watchlist.");
        }

        return results;
    }

    private void InjectFranchiseMates(
        List<MovieInfo> watch,
        HashSet<string> seen,
        string? genreFilter,
        float[] userVec,
        Dictionary<string, (MovieInfo Info, double Cosine)> candidates)
    {
        var stems = watch.Select(m => FranchiseTitle.Stem(m.Title)).Where(s => s.Length >= 3).Distinct().ToHashSet(StringComparer.Ordinal);
        if (stems.Count == 0)
            return;

        var injected = 0;
        foreach (var m in _catalog.Movies)
        {
            if (injected >= _options.MaxFranchiseInjects)
                break;
            if (seen.Contains(m.Id) || candidates.ContainsKey(m.Id))
                continue;
            if (genreFilter is not null && m.Genres.IndexOf(genreFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!stems.Contains(FranchiseTitle.Stem(m.Title)))
                continue;

            // Cosine vs user behaviour vector (same space as ANN).
            var emb = _encoder.Embed(m.EmbedText);
            var cosine = VectorMath.Dot(userVec, emb);
            candidates[m.Id] = (m, cosine);
            injected++;
        }
    }

    private static HashSet<string> BuildGenreSet(IEnumerable<MovieInfo> watch)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in watch)
        {
            foreach (var g in m.Genres.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(g);
        }
        return set;
    }

    private static double GenreJaccard(HashSet<string> watchGenres, string candidateGenres)
    {
        if (watchGenres.Count == 0)
            return 0;
        var cand = candidateGenres.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (cand.Length == 0)
            return 0;
        var inter = cand.Count(g => watchGenres.Contains(g));
        var union = watchGenres.Count;
        foreach (var g in cand)
        {
            if (!watchGenres.Contains(g))
                union++;
        }
        return union == 0 ? 0 : inter / (double)union;
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
