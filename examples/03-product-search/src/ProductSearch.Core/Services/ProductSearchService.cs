using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Storage;
using ProductSearch.Shared.Dtos;
using ProductSearch.Shared.Enums;
using ZVec.NET;
using ZVec.NET.Query;

namespace ProductSearch.Core.Services;

public interface IProductSearchService
{
    Task<SearchResponseDto> SearchAsync(SearchRequestDto request, CancellationToken ct = default);
}

public sealed class ProductSearchService : IProductSearchService
{
    private readonly DualCollectionHolder _collections;
    private readonly ISigLipEncoder _encoder;
    private readonly ISigLipModelSelectionService _models;
    private readonly IIndexStampStore _stamp;
    private readonly IDbContextFactory<ProductDbContext> _dbFactory;
    private readonly IProcessRuntimeMonitor _runtime;
    private readonly IRemoteImageFetcher _remoteImages;
    private readonly ProductSearchOptions _options;

    public ProductSearchService(
        DualCollectionHolder collections,
        ISigLipEncoder encoder,
        ISigLipModelSelectionService models,
        IIndexStampStore stamp,
        IDbContextFactory<ProductDbContext> dbFactory,
        IProcessRuntimeMonitor runtime,
        IRemoteImageFetcher remoteImages,
        IOptions<ProductSearchOptions> options)
    {
        _collections = collections;
        _encoder = encoder;
        _models = models;
        _stamp = stamp;
        _dbFactory = dbFactory;
        _runtime = runtime;
        _remoteImages = remoteImages;
        _options = options.Value;
    }

    public async Task<SearchResponseDto> SearchAsync(SearchRequestDto request, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var active = _models.ActiveDefinition;
        if (_stamp.IsMismatch(active))
        {
            return new SearchResponseDto
            {
                Warning = _stamp.MismatchMessage(active) + " Reset indexes → Ingest.",
                Runtime = _runtime.Capture()
            };
        }

        if (!_encoder.IsReady)
        {
            return new SearchResponseDto
            {
                Warning = _encoder.NotReadyReason ?? "Encoder not ready.",
                Runtime = _runtime.Capture()
            };
        }

        var topK = request.TopK > 0 ? request.TopK : _options.DefaultTopK;
        var minCosine = _options.MinCosine;
        var stamp = _stamp.Load();
        var (textCount, imageCount) = _collections.DocCounts;

        if (stamp.IngestOffset == 0 && (textCount > 0 || imageCount > 0))
        {
            return new SearchResponseDto
            {
                Warning =
                    $"SQL catalog is empty but ZVec still has {textCount} text / {imageCount} image docs. " +
                    "Start ingest (or Reset catalog) to rebuild both stores together.",
                Runtime = _runtime.Capture()
            };
        }

        var encodeSw = Stopwatch.StartNew();
        var queryVector = await ResolveQueryVectorAsync(request, ct).ConfigureAwait(false);
        encodeSw.Stop();

        IReadOnlyList<SearchHitDto> zvecHits = [];
        IReadOnlyList<SearchHitDto> pgHits = [];
        CompareMetricsDto? compare = null;
        double textAnnMs = 0;
        double imageAnnMs = 0;
        double ftsMs = 0;
        double fuseMs = 0;
        double pgMs = 0;
        double sqlHydrateMs = 0;
        string? mismatchWarning = null;

        if (request.Engine is VectorEngineMode.ZVec or VectorEngineMode.Both)
        {
            var zvecResult = await SearchZVecAsync(request, queryVector, topK, ct).ConfigureAwait(false);
            textAnnMs = zvecResult.TextAnnMs;
            imageAnnMs = zvecResult.ImageAnnMs;
            ftsMs = zvecResult.FtsMs;
            fuseMs = zvecResult.FuseMs;
            var filtered = ApplyConfidenceFilter(zvecResult.Hits, topK, minCosine);
            var hydrateSw = Stopwatch.StartNew();
            zvecHits = await HydrateAsync(filtered, "zvec", ct).ConfigureAwait(false);
            hydrateSw.Stop();
            sqlHydrateMs += hydrateSw.Elapsed.TotalMilliseconds;
            if (filtered.Count > 0 && zvecHits.Count == 0)
            {
                mismatchWarning =
                    "ZVec returned matches but no product rows exist in Postgres to display. Start ingest to rebuild SQL.";
            }
        }

        if (request.Engine is VectorEngineMode.Postgres or VectorEngineMode.Both)
        {
            var pgSw = Stopwatch.StartNew();
            var raw = await SearchPostgresAsync(request, queryVector, topK, ct).ConfigureAwait(false);
            pgSw.Stop();
            pgMs = pgSw.Elapsed.TotalMilliseconds;
            var filtered = ApplyConfidenceFilter(raw, topK, minCosine);
            var hydrateSw = Stopwatch.StartNew();
            pgHits = await HydrateAsync(filtered, "postgres", ct).ConfigureAwait(false);
            hydrateSw.Stop();
            sqlHydrateMs += hydrateSw.Elapsed.TotalMilliseconds;
            if (filtered.Count > 0 && pgHits.Count == 0 && mismatchWarning is null)
            {
                mismatchWarning =
                    "Postgres ANN returned matches but product rows could not be loaded. Check catalog state.";
            }
        }

        if (request.Engine == VectorEngineMode.Both)
            compare = BuildCompareMetrics(zvecHits, pgHits, textAnnMs + imageAnnMs + ftsMs + fuseMs, pgMs);

        totalSw.Stop();

        var primaryHits = request.Engine == VectorEngineMode.Postgres ? pgHits : zvecHits;
        string? warning = mismatchWarning;
        if (warning is null && primaryHits.Count == 0)
            warning = "No confident matches. Try another query or ingest more products.";

        return new SearchResponseDto
        {
            ZVecHits = request.Engine is VectorEngineMode.ZVec or VectorEngineMode.Both ? zvecHits : [],
            PostgreSqlHits = request.Engine is VectorEngineMode.Postgres or VectorEngineMode.Both ? pgHits : [],
            Compare = compare,
            Latency = new LatencyHudDto
            {
                EncodeMs = encodeSw.Elapsed.TotalMilliseconds,
                TextAnnMs = textAnnMs,
                ImageAnnMs = imageAnnMs,
                FtsMs = ftsMs,
                FuseMs = fuseMs,
                PgVectorMs = pgMs,
                SqlHydrateMs = sqlHydrateMs,
                TotalMs = totalSw.Elapsed.TotalMilliseconds
            },
            Runtime = _runtime.Capture(),
            Warning = warning
        };
    }

    private async Task<float[]> ResolveQueryVectorAsync(SearchRequestDto request, CancellationToken ct)
    {
        if (request.SimilarToProductId is Guid similarId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == similarId, ct)
                      .ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Product {similarId} not found.");
            var source = request.QueryMode == QueryMode.Image ? row.ImageEmbedding : row.TextEmbedding;
            if (source is null)
                throw new InvalidOperationException("Stored embedding missing for similar-to query.");
            return source.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            var bytes = Convert.FromBase64String(request.ImageBase64);
            using var ms = new MemoryStream(bytes);
            return _encoder.EncodeImage(ms);
        }

        if (!string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            await using var stream = await _remoteImages.FetchImageAsync(request.ImageUrl, ct).ConfigureAwait(false);
            return _encoder.EncodeImage(stream);
        }

        if (string.IsNullOrWhiteSpace(request.QueryText))
            throw new ArgumentException("Query text, image, or image URL is required.");

        return _encoder.EncodeText(request.QueryText.Trim());
    }

    private sealed record ZVecSearchResult(
        List<InternalHit> Hits,
        double TextAnnMs,
        double ImageAnnMs,
        double FtsMs,
        double FuseMs);

    private async Task<ZVecSearchResult> SearchZVecAsync(
        SearchRequestDto request,
        float[] queryVector,
        int topK,
        CancellationToken ct)
    {
        var filter = SearchInvertFilter.BuildZVecFilter(request);
        var textHits = new List<InternalHit>();
        var imageHits = new List<InternalHit>();
        double textAnnMs = 0;
        double imageAnnMs = 0;
        double ftsMs = 0;
        double fuseMs = 0;

        if (request.QueryMode is QueryMode.Text or QueryMode.Hybrid)
        {
            var textResult = await QueryTextCollectionAsync(request, queryVector, topK, filter, ct).ConfigureAwait(false);
            textHits.AddRange(textResult.Hits);
            textAnnMs = textResult.TextAnnMs;
            ftsMs = textResult.FtsMs;
            fuseMs = textResult.FuseMs;
        }

        if (request.QueryMode is QueryMode.Image or QueryMode.Hybrid)
        {
            var imageSw = Stopwatch.StartNew();
            var dense = await _collections.QueryImageDenseAsync(queryVector, topK, filter, ct).ConfigureAwait(false);
            imageSw.Stop();
            imageAnnMs = imageSw.Elapsed.TotalMilliseconds;
            var imageInternal = dense.Select(h => new InternalHit(h.Id, h.Score, false, true, false)).ToList();
            if (request.UseInvertFilter)
                imageInternal = await PostFilterByInvertAsync(imageInternal, request, ct).ConfigureAwait(false);
            imageHits.AddRange(imageInternal);
        }

        if (request.QueryMode is QueryMode.Hybrid)
        {
            var fuseSw = Stopwatch.StartNew();
            var fused = FuseHits(textHits, imageHits, request.Fusion, topK);
            fuseSw.Stop();
            fuseMs += fuseSw.Elapsed.TotalMilliseconds;
            return new ZVecSearchResult(fused, textAnnMs, imageAnnMs, ftsMs, fuseMs);
        }

        var combined = FuseHits(textHits, imageHits, request.Fusion, topK);
        return new ZVecSearchResult(combined, textAnnMs, imageAnnMs, ftsMs, fuseMs);
    }

    private sealed record TextQueryResult(List<InternalHit> Hits, double TextAnnMs, double FtsMs, double FuseMs);

    private async Task<TextQueryResult> QueryTextCollectionAsync(
        SearchRequestDto request,
        float[] queryVector,
        int topK,
        string? filter,
        CancellationToken ct)
    {
        var useHybridFts = request.UseHybridFts && !string.IsNullOrWhiteSpace(request.QueryText);
        if (!useHybridFts)
        {
            var annSw = Stopwatch.StartNew();
            var dense = await _collections.QueryTextDenseAsync(queryVector, topK, filter, ct).ConfigureAwait(false);
            annSw.Stop();
            return new TextQueryResult(
                dense.Select(h => new InternalHit(h.Id, h.Score, true, false, false)).ToList(),
                annSw.Elapsed.TotalMilliseconds,
                0,
                0);
        }

        var annOnlySw = Stopwatch.StartNew();
        var annHits = await _collections.QueryTextDenseAsync(queryVector, topK, filter, ct).ConfigureAwait(false);
        annOnlySw.Stop();

        var ftsSw = Stopwatch.StartNew();
        var ftsHits = await _collections.QueryTextUntypedAsync(
            [new ZVecQuery
            {
                FieldName = "ConcatenatedText",
                Fts = new ZVecFtsQuery
                {
                    QueryString = request.QueryText!.Trim(),
                    DefaultOperator = ZVecFtsDefaultOperator.Or
                }
            }],
            filter,
            reranker: null,
            topK,
            ct).ConfigureAwait(false);
        ftsSw.Stop();

        var fuseSw = Stopwatch.StartNew();
        var fused = FuseAnnAndFts(annHits, ftsHits, request.Fusion, topK);
        fuseSw.Stop();

        return new TextQueryResult(fused, annOnlySw.Elapsed.TotalMilliseconds, ftsSw.Elapsed.TotalMilliseconds, fuseSw.Elapsed.TotalMilliseconds);
    }

    private List<InternalHit> FuseAnnAndFts(
        IReadOnlyList<(string Id, float Score)> annHits,
        IReadOnlyList<(string Id, float Score)> ftsHits,
        FusionMode mode,
        int topK)
    {
        if (mode == FusionMode.Weighted)
        {
            var map = new Dictionary<string, InternalHit>(StringComparer.Ordinal);
            foreach (var hit in annHits)
            {
                var score = hit.Score * _options.DenseFusionWeight;
                map[hit.Id] = new InternalHit(hit.Id, score, true, false, false);
            }

            foreach (var hit in ftsHits)
            {
                var score = hit.Score * _options.FtsFusionWeight;
                if (!map.TryGetValue(hit.Id, out var existing))
                {
                    map[hit.Id] = new InternalHit(hit.Id, score, true, false, true);
                    continue;
                }

                map[hit.Id] = existing with { Score = Math.Min(existing.Score, score), FromFts = true };
            }

            return map.Values.OrderBy(h => h.Score).Take(topK).ToList();
        }

        var ranks = new Dictionary<string, (int AnnRank, int FtsRank)>(StringComparer.Ordinal);
        for (var i = 0; i < annHits.Count; i++)
            ranks[annHits[i].Id] = (i + 1, ranks.TryGetValue(annHits[i].Id, out var r) ? r.FtsRank : 0);
        for (var i = 0; i < ftsHits.Count; i++)
        {
            if (ranks.TryGetValue(ftsHits[i].Id, out var r))
                ranks[ftsHits[i].Id] = (r.AnnRank, i + 1);
            else
                ranks[ftsHits[i].Id] = (0, i + 1);
        }

        const float k = 60f;
        return ranks
            .Select(pair =>
            {
                var (annRank, ftsRank) = pair.Value;
                var rrf = 0f;
                if (annRank > 0) rrf += 1f / (k + annRank);
                if (ftsRank > 0) rrf += 1f / (k + ftsRank);
                return new InternalHit(pair.Key, -rrf, true, false, ftsRank > 0);
            })
            .OrderBy(h => h.Score)
            .Take(topK)
            .ToList();
    }

    private async Task<List<InternalHit>> PostFilterByInvertAsync(
        IReadOnlyList<InternalHit> hits,
        SearchRequestDto request,
        CancellationToken ct)
    {
        if (hits.Count == 0)
            return [];

        var ids = hits.Select(h => Guid.Parse(h.Id)).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Products.AsNoTracking().Where(p => ids.Contains(p.Id));
        q = SearchInvertFilter.ApplyPostgres(q, request);
        var allowed = await q.Select(p => p.Id).ToHashSetAsync(ct).ConfigureAwait(false);
        return hits.Where(h => allowed.Contains(Guid.Parse(h.Id))).ToList();
    }

    private static List<InternalHit> FuseHits(
        IReadOnlyList<InternalHit> textHits,
        IReadOnlyList<InternalHit> imageHits,
        FusionMode mode,
        int topK)
    {
        var map = new Dictionary<string, InternalHit>(StringComparer.Ordinal);
        void AddHits(IEnumerable<InternalHit> hits, float weight)
        {
            foreach (var hit in hits)
            {
                var score = mode == FusionMode.Weighted ? hit.Score * weight : hit.Score;
                if (!map.TryGetValue(hit.Id, out var existing))
                {
                    map[hit.Id] = hit with { Score = score };
                    continue;
                }

                map[hit.Id] = existing with
                {
                    Score = Math.Min(existing.Score, score),
                    FromText = existing.FromText || hit.FromText,
                    FromImage = existing.FromImage || hit.FromImage,
                    FromFts = existing.FromFts || hit.FromFts
                };
            }
        }

        AddHits(textHits, 0.5f);
        AddHits(imageHits, 0.5f);

        return [.. map.Values.OrderBy(h => h.Score).Take(topK)];
    }

    private async Task<List<InternalHit>> SearchPostgresAsync(
        SearchRequestDto request,
        float[] queryVector,
        int topK,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var vector = new Vector(queryVector);
        IQueryable<ProductEntity> q = db.Products.AsNoTracking();
        q = SearchInvertFilter.ApplyPostgres(q, request);

        var isImageSearch = request.QueryMode == QueryMode.Image
                            || !string.IsNullOrWhiteSpace(request.ImageBase64)
                            || !string.IsNullOrWhiteSpace(request.ImageUrl);

        if (isImageSearch)
        {
            var rows = await q.Where(p => p.ImageEmbedding != null)
                .Select(p => new { p.Id, Distance = p.ImageEmbedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .Take(topK)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return [.. rows.Select(r => new InternalHit(r.Id.ToString(), (float)r.Distance, false, true, false))];
        }

        var textRows = await q.Where(p => p.TextEmbedding != null)
            .Select(p => new { p.Id, Distance = p.TextEmbedding!.CosineDistance(vector) })
            .OrderBy(x => x.Distance)
            .Take(topK)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. textRows.Select(r => new InternalHit(r.Id.ToString(), (float)r.Distance, true, false, false))];
    }

    private async Task<IReadOnlyList<SearchHitDto>> HydrateAsync(
        IReadOnlyList<InternalHit> hits,
        string engine,
        CancellationToken ct)
    {
        if (hits.Count == 0)
            return [];

        var ids = hits.Select(h => Guid.Parse(h.Id)).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct)
            .ConfigureAwait(false);

        var result = new List<SearchHitDto>();
        var rank = 1;
        foreach (var hit in hits.OrderBy(h => h.Score))
        {
            if (!rows.TryGetValue(Guid.Parse(hit.Id), out var row))
                continue;

            var cosine = SigLipScoreSemantics.CosineFromZVecScore(hit.Score);
            result.Add(new SearchHitDto
            {
                Product = ToCard(row),
                Score = hit.Score,
                SimilarityPercent = SigLipScoreSemantics.SimilarityPercent(cosine),
                Rank = rank++,
                FromText = hit.FromText,
                FromImage = hit.FromImage,
                FromFts = hit.FromFts,
                Engine = engine
            });
        }

        return result;
    }

    private static ProductCardDto ToCard(ProductEntity row)
        => new()
        {
            Id = row.Id,
            CatalogId = int.TryParse(row.CatalogId, out var cid) ? cid : 0,
            ProductDisplayName = row.ProductDisplayName,
            Gender = row.Gender,
            MasterCategory = row.MasterCategory,
            SubCategory = row.SubCategory,
            ArticleType = row.ArticleType,
            BaseColour = row.BaseColour,
            Season = row.Season,
            Year = row.Year,
            Usage = row.Usage,
            ConcatenatedText = row.ConcatenatedText,
            ImageUrl = $"/api/media/{row.CatalogId}"
        };

    private List<InternalHit> ApplyConfidenceFilter(
        IReadOnlyList<InternalHit> hits,
        int topK,
        float minCosine)
    {
        if (hits.Count == 0)
            return [];

        var ordered = hits
            .Select(h => (Hit: h, Cosine: SigLipScoreSemantics.CosineFromZVecScore(h.Score)))
            .OrderByDescending(x => x.Cosine)
            .ToList();

        var topCosine = ordered[0].Cosine;
        if (topCosine < minCosine)
            return [];

        var gap = _options.MaxCosineGapFromTop;
        return [.. ordered
            .Where(x => x.Cosine >= minCosine && x.Cosine >= topCosine - gap)
            .Take(topK)
            .Select(x => x.Hit)];
    }

    private static CompareMetricsDto BuildCompareMetrics(
        IReadOnlyList<SearchHitDto> zvec,
        IReadOnlyList<SearchHitDto> pg,
        double zvecMs,
        double pgMs)
    {
        var zset = zvec.Select(h => h.Product.Id).ToHashSet();
        var pset = pg.Select(h => h.Product.Id).ToHashSet();
        var overlap = zset.Intersect(pset).Count();
        var union = zset.Union(pset).Count();
        var disagreements = 0;
        var n = Math.Min(zvec.Count, pg.Count);
        for (var i = 0; i < n; i++)
        {
            if (zvec[i].Product.Id != pg[i].Product.Id)
                disagreements++;
        }

        return new CompareMetricsDto
        {
            OverlapAtN = overlap,
            JaccardAtN = union > 0 ? (double)overlap / union : 0,
            RankDisagreements = disagreements,
            ZVecTotalMs = zvecMs,
            PostgreSqlTotalMs = pgMs
        };
    }

    private sealed record InternalHit(string Id, float Score, bool FromText, bool FromImage, bool FromFts);
}
