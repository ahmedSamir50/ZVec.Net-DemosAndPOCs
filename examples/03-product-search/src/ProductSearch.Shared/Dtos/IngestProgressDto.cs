namespace ProductSearch.Shared.Dtos;

/// <summary>Ingest saga progress snapshot.</summary>
public sealed class IngestProgressDto
{
    public string Status { get; set; } = "Idle";
    public int PatchSize { get; set; }
    public int PatchIndex { get; set; }
    public int Encoded { get; set; }
    public int ZVecUpserted { get; set; }
    public int SqlCommitted { get; set; }
    public int IngestOffset { get; set; }
    public string? ErrorMessage { get; set; }
}
