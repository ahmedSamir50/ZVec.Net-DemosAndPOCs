namespace ProductSearch.Shared.Dtos;

/// <summary>Per-file SigLIP download progress.</summary>
public sealed class ModelFileProgressDto
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public long BytesReceived { get; set; }
    public long? BytesTotal { get; set; }
    public double? Percent { get; set; }
    public bool OnDisk { get; set; }
    public string FullPath { get; set; } = "";
}

/// <summary>SigLIP bootstrap snapshot for HUD and Status page.</summary>
public sealed class ModelBootstrapSnapshotDto
{
    public string State { get; set; } = "";
    public string ModelsDir { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Error { get; set; }
    public string? ErrorDetail { get; set; }
    public double? OverallPercent { get; set; }
    public IReadOnlyList<ModelFileProgressDto> Files { get; set; } = [];
}
