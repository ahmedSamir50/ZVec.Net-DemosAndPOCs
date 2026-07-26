using ClipOnnx.App.Encoding;
using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;
using ZVec.NET;

namespace ClipOnnx.App.Services;

public sealed record SearchHitDto(string Id, string Path, string FileName, float Score);

public interface IGallerySearchService
{
    Task<IReadOnlyList<SearchHitDto>> SearchTextAsync(string query, int topK, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHitDto>> SearchImageAsync(Stream image, int topK, CancellationToken ct = default);
}

/// <summary>
/// Multimodal gallery search against the Cosine HNSW index on <see cref="ImageAsset.Embedding"/>.
/// Flow: encode query (text or image) → QueryAsync → hit.Score is cosine similarity
/// on L2-normalized vectors (same space for both modalities).
/// </summary>
public sealed class GallerySearchService : IGallerySearchService
{
    private readonly IZvecCollection<ImageAsset> _collection;
    private readonly IClipEncoder _encoder;
    private readonly ClipOnnxOptions _options;

    public GallerySearchService(
        IZvecCollection<ImageAsset> collection,
        IClipEncoder encoder,
        IOptions<ClipOnnxOptions> options)
    {
        _collection = collection;
        _encoder = encoder;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SearchHitDto>> SearchTextAsync(string query, int topK, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query text is required.", nameof(query));

        topK = topK <= 0 ? _options.DefaultTopK : topK;
        // Text encoder → 512-d unit vector in the same space as indexed vision embeds.
        var vector = _encoder.EncodeText(query.Trim());
        var hits = await _collection.QueryAsync(a => a.Embedding, vector, topK, filter: null, includeVector: false, ct);
        return Map(hits);
    }

    public async Task<IReadOnlyList<SearchHitDto>> SearchImageAsync(Stream image, int topK, CancellationToken ct = default)
    {
        topK = topK <= 0 ? _options.DefaultTopK : topK;
        var vector = _encoder.EncodeImage(image);
        var hits = await _collection.QueryAsync(a => a.Embedding, vector, topK, filter: null, includeVector: false, ct);
        return Map(hits);
    }

    private static IReadOnlyList<SearchHitDto> Map(IReadOnlyList<ZVecHit<ImageAsset>> hits)
        => hits.Select(h => new SearchHitDto(
            h.Record.Id,
            h.Record.Path,
            Path.GetFileName(h.Record.Path),
            h.Score)).ToList();
}
