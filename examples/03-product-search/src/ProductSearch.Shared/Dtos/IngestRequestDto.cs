namespace ProductSearch.Shared.Dtos;

/// <summary>Start a catalog ingest patch.</summary>
public sealed class IngestRequestDto
{
    public int PatchSize { get; set; } = 100;
    public bool OptimizeAfterPatch { get; set; } = true;
}
