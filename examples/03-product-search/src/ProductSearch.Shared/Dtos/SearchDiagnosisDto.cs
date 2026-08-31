namespace ProductSearch.Shared.Dtos;

/// <summary>Both-engine rank probe: raw SDK vs PG before UI filtering.</summary>
public sealed class SearchDiagnosisDto
{
    public string Branch { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public int OverlapAt5 { get; set; }
    public bool IsImageQuery { get; set; }
    public float? PgTopZVecProbeCosine { get; set; }
    public IReadOnlyList<RankProbeHitDto> RawZVecTop { get; set; } = [];
    public IReadOnlyList<RankProbeHitDto> RawPgTop { get; set; } = [];
}

public sealed class RankProbeHitDto
{
    public string Id { get; set; } = "";
    public float Score { get; set; }
}
