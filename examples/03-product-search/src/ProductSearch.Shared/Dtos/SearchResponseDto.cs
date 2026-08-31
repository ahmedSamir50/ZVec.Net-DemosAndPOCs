namespace ProductSearch.Shared.Dtos;

/// <summary>Search response with optional dual-column results.</summary>
public sealed class SearchResponseDto
{
    public IReadOnlyList<SearchHitDto> ZVecHits { get; set; } = [];
    public IReadOnlyList<SearchHitDto> PostgreSqlHits { get; set; } = [];
    public LatencyHudDto Latency { get; set; } = new();
    public RuntimeSnapshotDto? Runtime { get; set; }
    public CompareMetricsDto? Compare { get; set; }
    /// <summary>Set when Engine is Both — raw SDK vs PG rank probe (see server logs).</summary>
    public SearchDiagnosisDto? Diagnosis { get; set; }
    public string? Warning { get; set; }
}
