namespace ProductSearch.Shared.Dtos;

/// <summary>Honest API-process runtime snapshot (SigLIP + ZVec + Kestrel).</summary>
public sealed class RuntimeSnapshotDto
{
    public string ActiveModelId { get; set; } = "";
    public int EmbeddingDim { get; set; }
    public string OnnxExecutionProvider { get; set; } = "CPU";
    public int OnnxIntraOpThreads { get; set; }
    public int ProcessorCount { get; set; }
    public double ProcessCpuPercent { get; set; }
    public long WorkingSetMb { get; set; }
    public long PrivateMemoryMb { get; set; }
    public long GcHeapMb { get; set; }
    public string ProcessLabel { get; set; } = "API process (SigLIP + ZVec + Kestrel)";
}
