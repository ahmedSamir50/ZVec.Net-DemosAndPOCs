using Microsoft.Extensions.Logging;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Storage;
using ProductSearch.Shared.Dtos;
using ProductSearch.Shared.Enums;

namespace ProductSearch.Core.Services;

/// <summary>
/// Both-engine rank probe: compare raw ZVec QueryAsync hits to PG CosineDistance before UI filtering.
/// </summary>
internal static class SearchRankDiagnostics
{
    public const int ProbeK = 5;
    public const int SdkPgOverlapThreshold = 3;

    public sealed record ProbeResult(SearchDiagnosisDto Diagnosis);

    public static async Task<ProbeResult> RunAsync(
        SearchRequestDto request,
        float[] queryVector,
        DualCollectionHolder collections,
        Func<SearchRequestDto, float[], int, CancellationToken, Task<IReadOnlyList<RankProbeHitDto>>> searchPostgresTopAsync,
        ILogger logger,
        CancellationToken ct)
    {
        var isImage = IsImageQuery(request);
        var useHybridFts = request.UseHybridFts && !string.IsNullOrWhiteSpace(request.QueryText);

        if (!isImage && useHybridFts)
        {
            var skipped = new SearchDiagnosisDto
            {
                Branch = "SkippedFtsOn",
                Recommendation = "Turn Hybrid FTS off for a clean dense rank probe.",
                IsImageQuery = false
            };
            logger.LogInformation(
                "Rank probe skipped — Hybrid FTS is on. Disable FTS for dense SDK vs PG comparison.");
            return new ProbeResult(skipped);
        }

        var filter = SearchInvertFilter.BuildZVecFilter(request);
        IReadOnlyList<(string Id, float Score)> rawZvec;
        if (isImage)
            rawZvec = await collections.QueryImageDenseAsync(queryVector, ProbeK, filter, ct).ConfigureAwait(false);
        else
            rawZvec = await collections.QueryTextDenseAsync(queryVector, ProbeK, filter, ct).ConfigureAwait(false);

        var rawPg = await searchPostgresTopAsync(request, queryVector, ProbeK, ct).ConfigureAwait(false);

        var zvecIds = rawZvec.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var pgIds = rawPg.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var overlap = zvecIds.Intersect(pgIds).Count();

        float? probeCosine = null;
        string branch;
        string recommendation;

        if (rawPg.Count == 0)
        {
            branch = "PgEmpty";
            recommendation = "Postgres embedding table has no hits for this query. Run ingest.";
        }
        else if (rawZvec.Count == 0)
        {
            branch = "ZVecEmpty";
            recommendation = "ZVec returned no hits. Reset indexes → ingest → Optimize.";
        }
        else
        {
            var pgTopId = rawPg[0].Id;
            var stored = collections.TryFetchDenseEmbedding(pgTopId, isImage);
            if (stored is not null && stored.Length == queryVector.Length)
                probeCosine = (float)VectorMath.Dot(queryVector, stored);

            if (overlap >= SdkPgOverlapThreshold)
            {
                branch = "SdkMatchesPg";
                recommendation =
                    "Raw ZVec and PG agree on top IDs — any UI mismatch was C# (score polarity / confidence filter). " +
                    "Dense hits now use distance→cosine ranking.";
            }
            else if (probeCosine is >= 0.5f)
            {
                branch = "SdkDiffersHighProbe";
                recommendation =
                    $"PG #1 id {pgTopId} has high cosine ({probeCosine:0.###}) in ZVec storage but ANN ranked other ids. " +
                    "Run Optimize indexes (or re-ingest) — flat buffer may not be merged into HNSW.";
            }
            else if (probeCosine is not null)
            {
                branch = "SdkDiffersLowProbe";
                recommendation =
                    $"PG #1 id {pgTopId} has low cosine ({probeCosine:0.###}) in ZVec storage — vectors differ from Postgres. " +
                    "Reset catalog and re-ingest both stores together.";
            }
            else
            {
                branch = "SdkDiffersMissingDoc";
                recommendation =
                    $"PG #1 id {pgTopId} not found in ZVec {(isImage ? "image" : "text")} collection. Check split-brain or ingest.";
            }
        }

        var diagnosis = new SearchDiagnosisDto
        {
            Branch = branch,
            Recommendation = recommendation,
            OverlapAt5 = overlap,
            IsImageQuery = isImage,
            PgTopZVecProbeCosine = probeCosine,
            RawZVecTop = rawZvec.Take(ProbeK).Select(h => new RankProbeHitDto { Id = h.Id, Score = h.Score }).ToList(),
            RawPgTop = rawPg.Take(ProbeK).ToList()
        };

        logger.LogInformation(
            "Rank probe branch={Branch} overlap@5={Overlap} image={Image} pgTopProbeCosine={ProbeCosine:0.###} " +
            "zvec=[{ZVec}] pg=[{Pg}] → {Recommendation}",
            branch,
            overlap,
            isImage,
            probeCosine ?? float.NaN,
            FormatHits(rawZvec),
            FormatHits(rawPg.Select(h => (h.Id, h.Score))),
            recommendation);

        return new ProbeResult(diagnosis);
    }

    public static bool IsImageQuery(SearchRequestDto request)
        => request.QueryMode == QueryMode.Image
           || !string.IsNullOrWhiteSpace(request.ImageBase64)
           || !string.IsNullOrWhiteSpace(request.ImageUrl);

    private static string FormatHits(IEnumerable<(string Id, float Score)> hits)
        => string.Join("; ", hits.Select(h => $"{h.Id[..Math.Min(8, h.Id.Length)]}={h.Score:0.###}"));
}
