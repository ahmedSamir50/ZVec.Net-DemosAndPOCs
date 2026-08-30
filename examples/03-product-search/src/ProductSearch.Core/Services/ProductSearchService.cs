using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Models;
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
    private readonly ProductSearchOptions _options;

    public ProductSearchService(
        DualCollectionHolder collections,
        ISigLipEncoder encoder,
        ISigLipModelSelectionService models,
        IIndexStampStore stamp,
        IDbContextFactory<ProductDbContext> dbFactory,
        IOptions<ProductSearchOptions> options)
    {
        _collections = collections;
        _encoder = encoder;
        _models = models;
        _stamp = stamp;
        _dbFactory = dbFactory;
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
                Warning = _stamp.MismatchMessage(active) + " Reset indexes → Ingest."
            };
        }

        if (!_encoder.IsReady)
        {
            return new SearchResponseDto
            {
                Warning = _encoder.NotReadyReason ?? "Encoder not ready."
            };
        }

        var topK = request.TopK > 0 ? request.TopK : _options.DefaultTopK;
        var minCosine = _options.MinCosine;

        var encodeSw = Stopwatch.StartNew();
        var queryVector = await ResolveQueryVectorAsync(request, ct).ConfigureAwait(false);
        encodeSw.Stop();

        IReadOnlyList<SearchHitDto> zvecHits = [];
        IReadOnlyList<SearchHitDto> pgHits = [];
        CompareMetricsDto? compare = null;
        double textAnnMs = 0;
        double imageAnnMs = 0;
        double fuseMs = 0;
        double pgMs = 0;

        if (request.Engine is VectorEngineMode.ZVec or VectorEngineMode.Both)
        {
            var fuseSw = Stopwatch.StartNew();
            var fused = await SearchZVecAsync(request, queryVector, topK, ct).ConfigureAwait(false);
            fuseSw.Stop();
            textAnnMs = fused.TextAnnMs;
            imageAnnMs = fused.ImageAnnMs;
            fuseMs = fuseSw.Elapsed.TotalMilliseconds;
            zvecHits = await HydrateAsync(ApplyConfidenceFilter(fused.Hits, topK, minCosine), "zvec", ct)
                .ConfigureAwait(false);
        }

        if (request.Engine is VectorEngineMode.Postgres or VectorEngineMode.Both)
        {
            var pgSw = Stopwatch.StartNew();
            var raw = await SearchPostgresAsync(request, queryVector, topK, ct).ConfigureAwait(false);
            pgSw.Stop();
            pgMs = pgSw.Elapsed.TotalMilliseconds;
            pgHits = await HydrateAsync(ApplyConfidenceFilter(raw, topK, minCosine), "postgres", ct)
                .ConfigureAwait(false);
        }

        if (request.Engine == VectorEngineMode.Both)
            compare = BuildCompareMetrics(zvecHits, pgHits, fuseMs, pgMs);

        totalSw.Stop();
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
                FuseMs = fuseMs,
                PgVectorMs = pgMs,
                TotalMs = totalSw.Elapsed.TotalMilliseconds
            },
            Warning = (request.Engine == VectorEngineMode.ZVec ? zvecHits : pgHits).Count == 0
                ? "No confident matches. Try another query or ingest more products."
                : null
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

        if (string.IsNullOrWhiteSpace(request.QueryText))
            throw new ArgumentException("Query text or image is required.");

        return _encoder.EncodeText(request.QueryText.Trim());
    }

    private async Task<(List<InternalHit> Hits, double TextAnnMs, double ImageAnnMs)> SearchZVecAsync(
        SearchRequestDto request,
        float[] queryVector,
        int topK,
        CancellationToken ct)
    {
        var textSw = Stopwatch.StartNew();
        var textHits = new List<InternalHit>();
        if (request.QueryMode is QueryMode.Text or QueryMode.Hybrid)
            textHits.AddRange(await QueryTextCollectionAsync(request, queryVector, topK, ct).ConfigureAwait(false));
        textSw.Stop();

        var imageSw = Stopwatch.StartNew();
        var imageHits = new List<InternalHit>();
        if (request.QueryMode is QueryMode.Image or QueryMode.Hybrid)
        {
            var dense = await _collections.QueryImageDenseAsync(queryVector, topK, ct).ConfigureAwait(false);
            imageHits.AddRange(dense.Select(h => new InternalHit(h.Id, h.Score, false, true, false)));
        }
        imageSw.Stop();

        return (FuseHits(textHits, imageHits, request.Fusion, topK), textSw.Elapsed.TotalMilliseconds, imageSw.Elapsed.TotalMilliseconds);
    }

    private async Task<List<InternalHit>> QueryTextCollectionAsync(
        SearchRequestDto request,
        float[] queryVector,
        int topK,
        CancellationToken ct)
    {
        var textCol = _collections.GetTextCollectionUntyped();
        var filter = BuildInvertFilter(request);
        var queries = new List<ZVecQuery>
        {
            new() { FieldName = "TextEmbedding", Vector = queryVector }
        };

        if (request.UseHybridFts && !string.IsNullOrWhiteSpace(request.QueryText))
        {
            queries.Add(new ZVecQuery
            {
                FieldName = "ConcatenatedText",
                Fts = new ZVecFtsQuery
                {
                    QueryString = request.QueryText.Trim(),
                    DefaultOperator = ZVecFtsDefaultOperator.Or
                }
            });
        }

        ZVecReranker reranker = request.Fusion == FusionMode.Weighted
            ? new ZVecWeightedReranker
            {
                TopN = topK,
                Weights = new Dictionary<string, float>
                {
                    ["TextEmbedding"] = _options.DenseFusionWeight,
                    ["ConcatenatedText"] = _options.FtsFusionWeight
                }
            }
            : new ZVecRrfReranker { TopN = topK };

        var docs = queries.Count == 1
            ? textCol.Query(queries[0], topk: topK, filter: filter, includeVector: false)
            : textCol.Query(queries, topk: topK, reranker: reranker, filter: filter, includeVector: false);

        return docs.Select(d => new InternalHit(d.Id, d.Score, true, false, queries.Count > 1)).ToList();
    }

    private static string? BuildInvertFilter(SearchRequestDto request)
    {
        if (!request.UseInvertFilter)
            return null;

        var builder = ZVecFilterBuilder.Create();
        var has = false;
        if (!string.IsNullOrWhiteSpace(request.Gender))
        {
            builder.Where("Gender", ZVecCompareOp.Eq, request.Gender);
            has = true;
        }
        if (!string.IsNullOrWhiteSpace(request.BaseColour))
        {
            if (has) builder.And(f => f.Where("BaseColour", ZVecCompareOp.Eq, request.BaseColour));
            else { builder.Where("BaseColour", ZVecCompareOp.Eq, request.BaseColour); has = true; }
        }
        if (!string.IsNullOrWhiteSpace(request.Season))
        {
            if (has) builder.And(f => f.Where("Season", ZVecCompareOp.Eq, request.Season));
            else { builder.Where("Season", ZVecCompareOp.Eq, request.Season); has = true; }
        }
        if (!string.IsNullOrWhiteSpace(request.Usage))
        {
            if (has) builder.And(f => f.Where("Usage", ZVecCompareOp.Eq, request.Usage));
            else builder.Where("Usage", ZVecCompareOp.Eq, request.Usage);
        }

        return has ? builder.Build() : null;
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

        return map.Values.OrderBy(h => h.Score).Take(topK).ToList();
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

        if (!string.IsNullOrWhiteSpace(request.Gender))
            q = q.Where(p => p.Gender == request.Gender);
        if (!string.IsNullOrWhiteSpace(request.BaseColour))
            q = q.Where(p => p.BaseColour == request.BaseColour);
        if (!string.IsNullOrWhiteSpace(request.Season))
            q = q.Where(p => p.Season == request.Season);
        if (!string.IsNullOrWhiteSpace(request.Usage))
            q = q.Where(p => p.Usage == request.Usage);
        if (!string.IsNullOrWhiteSpace(request.MasterCategory))
            q = q.Where(p => p.MasterCategory == request.MasterCategory);

        var useImageColumn = request.QueryMode == QueryMode.Image
                             || !string.IsNullOrWhiteSpace(request.ImageBase64);

        var rows = useImageColumn
            ? await q.Where(p => p.ImageEmbedding != null)
                .OrderBy(p => p.ImageEmbedding!.CosineDistance(vector))
                .Take(topK)
                .ToListAsync(ct)
                .ConfigureAwait(false)
            : await q.Where(p => p.TextEmbedding != null)
                .OrderBy(p => p.TextEmbedding!.CosineDistance(vector))
                .Take(topK)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return rows.Select(r =>
        {
            var distance = useImageColumn
                ? (float)r.ImageEmbedding!.CosineDistance(vector)
                : (float)r.TextEmbedding!.CosineDistance(vector);
            return new InternalHit(r.Id.ToString(), distance, !useImageColumn, useImageColumn, false);
        }).ToList();
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

    private static List<InternalHit> ApplyConfidenceFilter(
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

        var gap = _optionsGap;
        return ordered
            .Where(x => x.Cosine >= minCosine && x.Cosine >= topCosine - gap)
            .Take(topK)
            .Select(x => x.Hit)
            .ToList();
    }

    private const float _optionsGap = 0.12f;

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
