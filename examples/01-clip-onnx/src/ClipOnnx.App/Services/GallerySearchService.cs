using System.Diagnostics;
using ClipOnnx.App.Encoding;
using ClipOnnx.App.DataModels;
using ClipOnnx.App.Options;
using ClipOnnx.App.Storage;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Services;

/// <summary>
/// One gallery hit.
/// <see cref="Score"/> is ZVec Cosine <b>distance</b> (lower = more similar).
/// <see cref="Cosine"/> is CLIP-style cosθ = 1 − distance (higher = better).
/// <see cref="SimilarityPercent"/> is display % from cosine (≥0).
/// <see cref="Caption"/> is optional Flickr text for UI only — not used to rank.
/// </summary>
public sealed record SearchHitDto(
    string Id,
    string Path,
    string FileName,
    float Score,
    float Cosine,
    int SimilarityPercent,
    string? Caption = null);

public sealed record SearchResponse(
    IReadOnlyList<SearchHitDto> Hits,
    string? PromptSummary,
    IReadOnlyList<string> TemplatesUsed,
    int RawHitCount,
    int FilteredOut,
    string? EmptyMessage,
    long? EncodeMs = null,
    long? QueryMs = null,
    string? ActiveModelId = null,
    int? EmbeddingDim = null);

public interface IGallerySearchService
{
    Task<SearchResponse> SearchTextAsync(string query, int topK, float? minCosine = null, CancellationToken ct = default);
    Task<SearchResponse> SearchImageAsync(Stream image, int topK, float? minCosine = null, CancellationToken ct = default);
    Task<SearchResponse> ProbeAsync(string query, int topK = 5, CancellationToken ct = default);
}

/// <summary>
/// Multimodal gallery search against the Cosine HNSW index on vision embeddings.
/// ZVec Cosine hit scores are <b>distances</b> (lower better); convert with
/// <see cref="ClipScoreSemantics.CosineFromZVecScore"/> before showing % or filtering.
/// Results are ordered best→worst by true CLIP cosine.
/// Refuses to search when the gallery stamp does not match the active CLIP model.
/// </summary>
public sealed class GallerySearchService : IGallerySearchService
{
    private static readonly HashSet<string> DeniedBareQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        "network", "19"
    };

    private readonly GalleryStore _gallery;
    private readonly IClipEncoder _encoder;
    private readonly IGalleryStampStore _stamp;
    private readonly IClipModelSelectionService _models;
    private readonly IFlickrCaptionLookup _captions;
    private readonly ClipOnnxOptions _options;

    public GallerySearchService(
        GalleryStore gallery,
        IClipEncoder encoder,
        IGalleryStampStore stamp,
        IClipModelSelectionService models,
        IFlickrCaptionLookup captions,
        IOptions<ClipOnnxOptions> options)
    {
        _gallery = gallery;
        _encoder = encoder;
        _stamp = stamp;
        _models = models;
        _captions = captions;
        _options = options.Value;
    }

    public async Task<SearchResponse> SearchTextAsync(
        string query,
        int topK,
        float? minCosine = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query text is required.", nameof(query));

        if (MismatchResponse() is { } blocked)
            return blocked;

        var trimmed = query.Trim();
        if (DeniedBareQueries.Contains(trimmed))
        {
            return Empty(
                "That bare query is blocked for demos (too abstract / trap). " +
                "Try a chip like “dogs in the snow” or “people on a beach”.");
        }

        topK = topK <= 0 ? _options.DefaultTopK : topK;
        var swEnc = Stopwatch.StartNew();
        var (vector, templates, summary) = EncodeTextEnsemble(trimmed);
        swEnc.Stop();

        var fetchK = Math.Min(Math.Max(topK * 3, topK), 50);
        var swQ = Stopwatch.StartNew();
        var hits = await _gallery.QueryAsync(vector, fetchK, ct);
        swQ.Stop();

        return BuildResponse(hits, topK, minCosine ?? _options.MinCosine, summary, templates, swEnc.ElapsedMilliseconds, swQ.ElapsedMilliseconds);
    }

    public async Task<SearchResponse> SearchImageAsync(
        Stream image,
        int topK,
        float? minCosine = null,
        CancellationToken ct = default)
    {
        if (MismatchResponse() is { } blocked)
            return blocked;

        topK = topK <= 0 ? _options.DefaultTopK : topK;
        var swEnc = Stopwatch.StartNew();
        var vector = _encoder.EncodeImage(image);
        swEnc.Stop();

        var fetchK = Math.Min(Math.Max(topK * 3, topK), 50);
        var swQ = Stopwatch.StartNew();
        var hits = await _gallery.QueryAsync(vector, fetchK, ct);
        swQ.Stop();

        return BuildResponse(hits, topK, minCosine ?? _options.MinCosine, null, [], swEnc.ElapsedMilliseconds, swQ.ElapsedMilliseconds);
    }

    public async Task<SearchResponse> ProbeAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query text is required.", nameof(query));

        if (MismatchResponse() is { } blocked)
            return blocked;

        topK = topK <= 0 ? 5 : topK;
        var (vector, templates, summary) = EncodeTextEnsemble(query.Trim());
        var hits = await _gallery.QueryAsync(vector, topK, ct);
        var mapped = Map(hits).OrderByDescending(h => h.Cosine).ToList();
        var def = _models.ActiveDefinition;
        return new SearchResponse(mapped, summary, templates, mapped.Count, 0, null, null, null, def.Id, def.EmbeddingDim);
    }

    private SearchResponse? MismatchResponse()
    {
        var def = _models.ActiveDefinition;
        var msg = _stamp.MismatchMessage(def);
        if (msg is null)
            return null;
        return new SearchResponse(
            [],
            null,
            [],
            0,
            0,
            msg + " Use Reset index → Ingest.",
            ActiveModelId: def.Id,
            EmbeddingDim: def.EmbeddingDim);
    }

    private SearchResponse Empty(string message)
    {
        var def = _models.ActiveDefinition;
        return new SearchResponse([], null, [], 0, 0, message, ActiveModelId: def.Id, EmbeddingDim: def.EmbeddingDim);
    }

    private (float[] Vector, IReadOnlyList<string> Templates, string Summary) EncodeTextEnsemble(string query)
    {
        var templates = ResolveTemplates(query);
        if (templates.Count == 1)
        {
            var v = _encoder.EncodeText(templates[0]);
            return (v, templates, templates[0]);
        }

        var vectors = templates.Select(t => _encoder.EncodeText(t)).ToList();
        var mean = VectorMath.AverageThenL2Normalize(vectors);
        return (mean, templates, string.Join(" · ", templates));
    }

    private IReadOnlyList<string> ResolveTemplates(string query)
    {
        if (query.StartsWith("a photo of", StringComparison.OrdinalIgnoreCase)
            || query.StartsWith("a picture of", StringComparison.OrdinalIgnoreCase))
            return [query];

        var configured = _options.TextPromptTemplates?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (configured is { Count: > 0 })
            return configured.Select(t => ApplyTemplate(t, query)).ToList();

        if (!string.IsNullOrWhiteSpace(_options.TextPromptTemplate))
            return [ApplyTemplate(_options.TextPromptTemplate, query)];

        return [query];
    }

    private static string ApplyTemplate(string template, string query)
    {
        if (template.Contains("{query}", StringComparison.Ordinal))
            return template.Replace("{query}", query, StringComparison.Ordinal);
        return $"{template.Trim()} {query}";
    }

    private SearchResponse BuildResponse(
        IReadOnlyList<GalleryQueryHit> raw,
        int topK,
        float minCosine,
        string? promptSummary,
        IReadOnlyList<string> templatesUsed,
        long encodeMs,
        long queryMs)
    {
        var def = _models.ActiveDefinition;
        // Map + sort by true CLIP cosine (higher better). ZVec returns distance-ascending;
        // after conversion we make display order explicit.
        var mapped = Map(raw)
            .OrderByDescending(h => h.Cosine)
            .ToList();
        var rawCount = mapped.Count;
        if (rawCount == 0)
        {
            return new SearchResponse(
                [],
                promptSummary,
                templatesUsed,
                0,
                0,
                "No hits in the index. Ingest images first (Reset index → Ingest after model changes).",
                encodeMs,
                queryMs,
                def.Id,
                def.EmbeddingDim);
        }

        var topCosine = mapped[0].Cosine;
        if (topCosine < minCosine)
        {
            return new SearchResponse(
                [],
                promptSummary,
                templatesUsed,
                rawCount,
                rawCount,
                $"No confident matches: top cos {topCosine:F3} < min {minCosine:F3} (rawHits={rawCount}). " +
                "Try a concrete chip query, or Reset+Ingest if dogs/snow also fail.",
                encodeMs,
                queryMs,
                def.Id,
                def.EmbeddingDim);
        }

        var gap = Math.Max(0f, _options.MaxCosineGapFromTop);
        var filtered = mapped
            .Where(h => h.Cosine >= minCosine && h.Cosine >= topCosine - gap)
            .Take(topK)
            .ToList();

        var passedCount = filtered.Count;
        var filteredOut = rawCount - passedCount;
        string? empty = null;
        var minHits = Math.Max(1, _options.MinConfidentHits);
        if (passedCount < minHits)
        {
            empty =
                $"No confident matches: only {passedCount} hit(s) passed min+gap " +
                $"(top cos {topCosine:F3}, min {minCosine:F3}, gap {gap:F3}, MinConfidentHits={minHits}, rawHits={rawCount}). " +
                "Try “dogs in the snow” or “people on a beach”. " +
                "If dogs/snow also fail, Reset index → Ingest with the active model.";
            filtered = [];
            filteredOut = rawCount;
        }

        return new SearchResponse(filtered, promptSummary, templatesUsed, rawCount, filteredOut, empty, encodeMs, queryMs, def.Id, def.EmbeddingDim);
    }

    private IReadOnlyList<SearchHitDto> Map(IReadOnlyList<GalleryQueryHit> hits)
    {
        _captions.EnsureLoaded();
        return hits.Select(h =>
        {
            var cosine = ClipScoreSemantics.CosineFromZVecScore(h.Score);
            var fileName = Path.GetFileName(h.Path);
            return new SearchHitDto(
                h.Id,
                h.Path,
                fileName,
                h.Score,
                cosine,
                ClipScoreSemantics.SimilarityPercent(cosine),
                _captions.GetCaption(fileName));
        }).ToList();
    }
}
