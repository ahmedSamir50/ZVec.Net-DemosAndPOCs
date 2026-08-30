namespace ProductSearch.Shared.Dtos;

/// <summary>Quality and speed comparison when engine mode is Both.</summary>
public sealed class CompareMetricsDto
{
    public int OverlapAtN { get; set; }
    public double JaccardAtN { get; set; }
    public int RankDisagreements { get; set; }
    public double ZVecTotalMs { get; set; }
    public double PostgreSqlTotalMs { get; set; }
}
