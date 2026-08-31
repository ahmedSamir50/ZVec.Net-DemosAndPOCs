namespace ProductSearch.Shared.Dtos;

/// <summary>Demo readiness and index counts.</summary>
public sealed class StatusDto
{
    public int PostgresCount { get; set; }
    public int ZVecTextCount { get; set; }
    public int ZVecImageCount { get; set; }
    public string ActiveModelId { get; set; } = "";
    public int EmbeddingDim { get; set; }
    public bool StampMatch { get; set; }
    public bool DemoReady { get; set; }
    public int IngestOffset { get; set; }
    public int CatalogTotal { get; set; }
    public bool ModelBootstrapComplete { get; set; }
    public ModelBootstrapSnapshotDto? ModelBootstrap { get; set; }
    public string? StampWarning { get; set; }
    public string? IndexWarning { get; set; }
    public PostgresConnectionDto? Postgres { get; set; }
}
