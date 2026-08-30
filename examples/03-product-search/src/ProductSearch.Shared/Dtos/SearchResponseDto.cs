namespace ProductSearch.Shared.Dtos;

/// <summary>Search response with optional dual-column results.</summary>
public sealed class SearchResponseDto
{
    public IReadOnlyList<SearchHitDto> ZVecHits { get; set; } = [];
    public IReadOnlyList<SearchHitDto> PostgreSqlHits { get; set; } = [];
    public LatencyHudDto Latency { get; set; } = new();
    public CompareMetricsDto? Compare { get; set; }
    public string? Warning { get; set; }
}
