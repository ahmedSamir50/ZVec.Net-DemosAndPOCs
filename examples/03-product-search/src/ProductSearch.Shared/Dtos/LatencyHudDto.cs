namespace ProductSearch.Shared.Dtos;

/// <summary>Per-stage latency breakdown in milliseconds.</summary>
public sealed class LatencyHudDto
{
    public double EncodeMs { get; set; }
    public double TextAnnMs { get; set; }
    public double ImageAnnMs { get; set; }
    public double FtsMs { get; set; }
    public double FuseMs { get; set; }
    public double PgVectorMs { get; set; }
    public double SqlHydrateMs { get; set; }
    public double TotalMs { get; set; }
}
