using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<ProductSearchService> _logger;

    public ProductSearchService(
        DualCollectionHolder collections,
        ISigLipEncoder encoder,
        ISigLipModelSelectionService models,
        IIndexStampStore stamp,
        IDbContextFactory<ProductDbContext> dbFactory,
        IProcessRuntimeMonitor runtime,
        IRemoteImageFetcher remoteImages,
        IOptions<ProductSearchOptions> options,
        ILogger<ProductSearchService> logger)
    {
        _collections = collections;
        _encoder = encoder;
        _models = models;
        _stamp = stamp;
        _dbFactory = dbFactory;
        _runtime = runtime;
        _remoteImages = remoteImages;
        _options = options.Value;
        _logger = logger;
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

        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var embeddingCount = await db.EmbeddingCountAsync(active.EmbeddingDim, ct).ConfigureAwait(false);

            if (CatalogStoreAlignment.HasSplitBrain(embeddingCount, textCount, imageCount))
            {
                return new SearchResponseDto
                {
                    Warning = CatalogStoreAlignment.SplitBrainMessage(embeddingCount, textCount, imageCount),
                    Runtime = _runtime.Capture()
                };
            }
        }

        if (stamp.IngestOffset == 0 && (textCount > 0 || imageCount > 0))
        {
            return new SearchResponseDto
            {
                Warning =
                    $"Ingest offset is 0 but ZVec still has {textCount} text / {imageCount} image docs. " +
                    "Reset indexes, then re-ingest.",
                Runtime = _runtime.Capture()
            };
        }

        var encodeSw = Stopwatch.StartNew();
        var queryVector = await ResolveQueryVectorAsync(request, ct).ConfigureAwait(false);
        encodeSw.Stop();

        SearchDiagnosisDto? diagnosis = null;
        string? diagnosisWarning = null;
        if (request.Engine == VectorEngineMode.Both)
        {
            var probe = await SearchRankDiagnostics.RunAsync(
                request,
                queryVector,
                _collections,
                async (req, vec, k, token) =>
                {
                    var hits = await SearchPostgresAsync(req, vec, k, token).ConfigureAwait(false);
                    return hits.Select(h => new RankProbeHitDto { Id = h.Id, Score = h.Score }).ToList();
                },
                _logger,
                ct).ConfigureAwait(false);
            diagnosis = probe.Diagnosis;
            if (probe.Diagnosis.Branch is "SdkDiffersHighProbe" or "SdkDiffersLowProbe" or "SdkDiffersMissingDoc" or "ZVecEmpty")
                diagnosisWarning = probe.Diagnosis.Recommendation;
        }

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
            LogSdkOrder("ZVec", zvecResult.Hits);
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
            LogSdkOrder("Postgres", raw);
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
        string? warning = mismatchWarning ?? diagnosisWarning;
        if (warning is null && primaryHits.Count == 0)
            warning = "No confident matches. Try another query or ingest more products.";

        return new SearchResponseDto
        {
            ZVecHits = request.Engine is VectorEngineMode.ZVec or VectorEngineMode.Both ? zvecHits : [],
            PostgreSqlHits = request.Engine is VectorEngineMode.Postgres or VectorEngineMode.Both ? pgHits : [],
            Compare = compare,
            Diagnosis = diagnosis,
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
            var exists = await db.Products.AsNoTracking().AnyAsync(p => p.Id == similarId, ct)
                .ConfigureAwait(false);
            if (!exists)
                throw new InvalidOperationException($"Product {similarId} not found.");

            var source = await LoadStoredEmbeddingAsync(
                    db, similarId, request.QueryMode == QueryMode.Image, ct)
                .ConfigureAwait(false);
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
            var imageInternal = ToDenseHits(dense, fromText: false, fromImage: true);
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

        if (textHits.Count > 0 && imageHits.Count == 0)
            return new ZVecSearchResult(textHits, textAnnMs, imageAnnMs, ftsMs, fuseMs);
        if (imageHits.Count > 0 && textHits.Count == 0)
            return new ZVecSearchResult(imageHits, textAnnMs, imageAnnMs, ftsMs, fuseMs);

        return new ZVecSearchResult(FuseHits(textHits, imageHits, request.Fusion, topK), textAnnMs, imageAnnMs, ftsMs, fuseMs);
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
            // In SigLIP / CLIP, text query embeddings match image embeddings in visual space
            var dense = await _collections.QueryImageDenseAsync(queryVector, topK, filter, ct).ConfigureAwait(false);
            annSw.Stop();
            return new TextQueryResult(
                ToDenseHits(dense, fromText: true, fromImage: true),
                annSw.Elapsed.TotalMilliseconds,
                0,
                0);
        }

        var fetchK = Math.Min(80, Math.Max(topK * 8, topK));
        var annOnlySw = Stopwatch.StartNew();
        // Cross-modal dense query against image collection
        var annHits = await _collections.QueryImageDenseAsync(queryVector, fetchK, filter, ct).ConfigureAwait(false);
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
            fetchK,
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
        var ann = ToDenseHits(annHits, fromText: true, fromImage: true);
        var annById = ann.ToDictionary(h => h.Id, StringComparer.Ordinal);

        if (mode == FusionMode.Weighted)
        {
            var map = new Dictionary<string, InternalHit>(StringComparer.Ordinal);
            foreach (var hit in ann)
                map[hit.Id] = hit with { DisplayCosine = hit.DisplayCosine * _options.DenseFusionWeight };

            foreach (var hit in ftsHits)
            {
                var boost = _options.FtsFusionWeight;
                if (!map.TryGetValue(hit.Id, out var existing))
                {
                    map[hit.Id] = new InternalHit(hit.Id, hit.Score, true, true, true, 0.20f + boost);
                    continue;
                }

                map[hit.Id] = existing with
                {
                    DisplayCosine = existing.DisplayCosine + boost,
                    FromFts = true
                };
            }

            return [.. map.Values.OrderByDescending(h => h.DisplayCosine).Take(topK)];
        }

        var ranks = new Dictionary<string, (int AnnRank, int FtsRank)>(StringComparer.Ordinal);
        for (var i = 0; i < annHits.Count; i++)
            ranks[annHits[i].Id] = (i + 1, 0);
        for (var i = 0; i < ftsHits.Count; i++)
        {
            if (ranks.TryGetValue(ftsHits[i].Id, out var r))
                ranks[ftsHits[i].Id] = (r.AnnRank, i + 1);
            else
                ranks[ftsHits[i].Id] = (0, i + 1);
        }

        const float k = 60f;
        return [.. ranks
            .Select(pair =>
            {
                var (annRank, ftsRank) = pair.Value;
                var rrf = 0f;
                if (annRank > 0) rrf += 1f / (k + annRank);
                if (ftsRank > 0) rrf += 1f / (k + ftsRank);
                // For hits matched by FTS without dense ANN, assign calibrated non-zero baseline
                var cosine = annById.TryGetValue(pair.Key, out var annHit) ? annHit.DisplayCosine : 0.20f;
                return new InternalHit(pair.Key, rrf, true, true, ftsRank > 0, cosine);
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)];
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
        if (textHits.Count == 0)
            return imageHits.Take(topK).ToList();
        if (imageHits.Count == 0)
            return textHits.Take(topK).ToList();

        var ranks = new Dictionary<string, (InternalHit Hit, int TextRank, int ImageRank)>(StringComparer.Ordinal);
        for (var i = 0; i < textHits.Count; i++)
        {
            var hit = textHits[i];
            ranks[hit.Id] = (hit, i + 1, 0);
        }

        for (var i = 0; i < imageHits.Count; i++)
        {
            var hit = imageHits[i];
            if (ranks.TryGetValue(hit.Id, out var r))
            {
                ranks[hit.Id] = (r.Hit with
                {
                    FromImage = true,
                    DisplayCosine = Math.Max(r.Hit.DisplayCosine, hit.DisplayCosine)
                }, r.TextRank, i + 1);
            }
            else
            {
                ranks[hit.Id] = (hit, 0, i + 1);
            }
        }

        if (mode == FusionMode.Weighted)
        {
            return [.. ranks.Values
                .Select(v => v.Hit with
                {
                    DisplayCosine = v.Hit.DisplayCosine * (v.TextRank > 0 && v.ImageRank > 0 ? 1f : 0.5f)
                })
                .OrderByDescending(h => h.DisplayCosine)
                .Take(topK)];
        }

        const float k = 60f;
        return [.. ranks
            .Select(pair =>
            {
                var (hit, textRank, imageRank) = pair.Value;
                var rrf = 0f;
                if (textRank > 0) rrf += 1f / (k + textRank);
                if (imageRank > 0) rrf += 1f / (k + imageRank);
                return hit with { Score = rrf };
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)];
    }

    private async Task<Vector?> LoadStoredEmbeddingAsync(
        ProductDbContext db,
        Guid id,
        bool image,
        CancellationToken ct)
    {
        var dim = _models.ActiveDefinition.EmbeddingDim;
        if (dim == 1152)
        {
            var row = await db.Embeddings1152.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id, ct)
                .ConfigureAwait(false);
            return image ? row?.ImageEmbedding : row?.TextEmbedding;
        }

        if (dim != 768)
            throw new InvalidOperationException($"Unsupported embedding dimension {dim}.");

        var row768 = await db.Embeddings768.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);
        return image ? row768?.ImageEmbedding : row768?.TextEmbedding;
    }

    private async Task<List<InternalHit>> SearchPostgresAsync(
        SearchRequestDto request,
        float[] queryVector,
        int topK,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var vector = new Vector(queryVector);
        var products = SearchInvertFilter.ApplyPostgres(db.Products.AsNoTracking(), request);
        var isImageSearch = request.QueryMode == QueryMode.Image
                            || !string.IsNullOrWhiteSpace(request.ImageBase64)
                            || !string.IsNullOrWhiteSpace(request.ImageUrl);

        var dim = _models.ActiveDefinition.EmbeddingDim;
        if (dim == 1152)
        {
            var q = db.Embeddings1152.AsNoTracking()
                .Join(products, e => e.Id, p => p.Id, (e, _) => e);
            return await QueryPostgresDistanceAsync(q, vector, topK, isImageSearch, ct).ConfigureAwait(false);
        }

        if (dim != 768)
            throw new InvalidOperationException($"Unsupported embedding dimension {dim}.");

        var q768 = db.Embeddings768.AsNoTracking()
            .Join(products, e => e.Id, p => p.Id, (e, _) => e);
        return await QueryPostgresDistanceAsync(q768, vector, topK, isImageSearch, ct).ConfigureAwait(false);
    }

    private static async Task<List<InternalHit>> QueryPostgresDistanceAsync(
        IQueryable<ProductEmbedding1152Entity> embeddings,
        Vector vector,
        int topK,
        bool isImageSearch,
        CancellationToken ct)
    {
        if (isImageSearch)
        {
            var rows = await embeddings.Where(e => e.ImageEmbedding != null)
                .Select(e => new { e.Id, Distance = e.ImageEmbedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .Take(topK)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return ToPgHits(rows.Select(r => (r.Id, r.Distance)), fromImage: true);
        }

        var textRows = await embeddings.Where(e => e.TextEmbedding != null)
            .Select(e => new { e.Id, Distance = e.TextEmbedding!.CosineDistance(vector) })
            .OrderBy(x => x.Distance)
            .Take(topK)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ToPgHits(textRows.Select(r => (r.Id, r.Distance)), fromImage: false);
    }

    private static async Task<List<InternalHit>> QueryPostgresDistanceAsync(
        IQueryable<ProductEmbedding768Entity> embeddings,
        Vector vector,
        int topK,
        bool isImageSearch,
        CancellationToken ct)
    {
        if (isImageSearch)
        {
            var rows = await embeddings.Where(e => e.ImageEmbedding != null)
                .Select(e => new { e.Id, Distance = e.ImageEmbedding!.CosineDistance(vector) })
                .OrderBy(x => x.Distance)
                .Take(topK)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return ToPgHits(rows.Select(r => (r.Id, r.Distance)), fromImage: true);
        }

        var textRows = await embeddings.Where(e => e.TextEmbedding != null)
            .Select(e => new { e.Id, Distance = e.TextEmbedding!.CosineDistance(vector) })
            .OrderBy(x => x.Distance)
            .Take(topK)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ToPgHits(textRows.Select(r => (r.Id, r.Distance)), fromImage: false);
    }

    private static List<InternalHit> ToPgHits(IEnumerable<(Guid Id, double Distance)> rows, bool fromImage)
        => [.. rows.Select(r => new InternalHit(
            r.Id.ToString(),
            (float)r.Distance,
            !fromImage,
            fromImage,
            false,
            SigLipScoreSemantics.CosineFromDistance((float)r.Distance)))];

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
        foreach (var hit in hits)
        {
            if (!rows.TryGetValue(Guid.Parse(hit.Id), out var row))
                continue;

            result.Add(new SearchHitDto
            {
                Product = ToCard(row),
                Score = hit.Score,
                SimilarityPercent = SigLipScoreSemantics.SimilarityPercent(hit.DisplayCosine),
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

        var topCosine = hits[0].DisplayCosine;
        var topIsFts = hits[0].FromFts;
        // Only drop entirely if the top hit is purely dense and below minimum confidence
        if (!topIsFts && topCosine < minCosine)
            return [];

        var gap = _options.MaxCosineGapFromTop;
        var kept = new List<InternalHit>(Math.Min(topK, hits.Count));
        foreach (var hit in hits)
        {
            if (kept.Count >= topK)
                break;
            // Never drop confirmed lexical FTS matches on cosine threshold
            if (!hit.FromFts && (hit.DisplayCosine < minCosine || hit.DisplayCosine < topCosine - gap))
                continue;
            kept.Add(hit);
        }

        return kept;
    }

    private void LogSdkOrder(string engine, IReadOnlyList<InternalHit> hits)
    {
        if (hits.Count == 0)
            return;

        var first = hits[0];
        var last = hits[^1];
        _logger.LogDebug(
            "{Engine} hit order: first={FirstId} raw={FirstScore:0.###} cosine={FirstCosine:0.###}; last={LastId} raw={LastScore:0.###} cosine={LastCosine:0.###} n={Count}",
            engine,
            first.Id,
            first.Score,
            first.DisplayCosine,
            last.Id,
            last.Score,
            last.DisplayCosine,
            hits.Count);
    }

    private static List<InternalHit> ToDenseHits(
        IReadOnlyList<(string Id, float Score)> dense,
        bool fromText,
        bool fromImage)
    {
        if (dense.Count == 0)
            return [];

        var list = dense.Select(hit => new InternalHit(
            hit.Id,
            hit.Score,
            fromText,
            fromImage,
            false,
            SigLipScoreSemantics.CosineFromDistance(hit.Score))).ToList();

        // ZVec Cosine Score is distance (same contract as CLIP/movie-recs). Sort by true cosine.
        list.Sort((a, b) => b.DisplayCosine.CompareTo(a.DisplayCosine));
        return list;
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

    private sealed record InternalHit(
        string Id,
        float Score,
        bool FromText,
        bool FromImage,
        bool FromFts,
        float DisplayCosine);
}
