using System.Diagnostics;
using ProductSearch.Core.Encoding;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Core.Services;

public interface IProcessRuntimeMonitor
{
    RuntimeSnapshotDto Capture();
}

/// <summary>Samples the current API process for honest CPU and memory telemetry.</summary>
public sealed class ProcessRuntimeMonitor : IProcessRuntimeMonitor
{
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    private readonly ISigLipEncoder _encoder;
    private readonly ISigLipModelSelectionService _models;
    private readonly object _cpuLock = new();
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private double _lastCpuPercent;

    public ProcessRuntimeMonitor(ISigLipEncoder encoder, ISigLipModelSelectionService models)
    {
        _encoder = encoder;
        _models = models;
        CurrentProcess.Refresh();
        _lastCpuTime = CurrentProcess.TotalProcessorTime;
        _lastSampleUtc = DateTime.UtcNow;
    }

    public RuntimeSnapshotDto Capture()
    {
        CurrentProcess.Refresh();

        var active = _models.ActiveDefinition;
        var modelId = _encoder.IsReady && !string.IsNullOrWhiteSpace(_encoder.ActiveModelId)
            ? _encoder.ActiveModelId!
            : active.Id;
        var dim = _encoder.IsReady ? _encoder.EmbeddingDim : active.EmbeddingDim;

        return new RuntimeSnapshotDto
        {
            ActiveModelId = modelId,
            EmbeddingDim = dim,
            OnnxExecutionProvider = "CPU",
            OnnxIntraOpThreads = _encoder.IntraOpNumThreads,
            ProcessorCount = Environment.ProcessorCount,
            ProcessCpuPercent = SampleCpuPercent(),
            WorkingSetMb = ToMb(CurrentProcess.WorkingSet64),
            PrivateMemoryMb = ToMb(CurrentProcess.PrivateMemorySize64),
            GcHeapMb = ToMb(GC.GetTotalMemory(forceFullCollection: false))
        };
    }

    private double SampleCpuPercent()
    {
        lock (_cpuLock)
        {
            var now = DateTime.UtcNow;
            var cpuTime = CurrentProcess.TotalProcessorTime;
            var elapsedMs = (now - _lastSampleUtc).TotalMilliseconds;

            if (elapsedMs >= 100)
            {
                var cpuDeltaMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                var cores = Math.Max(1, Environment.ProcessorCount);
                _lastCpuPercent = Math.Clamp(cpuDeltaMs / (elapsedMs * cores) * 100.0, 0, 100);
                _lastCpuTime = cpuTime;
                _lastSampleUtc = now;
            }

            return Math.Round(_lastCpuPercent, 1);
        }
    }

    private static long ToMb(long bytes)
        => bytes <= 0 ? 0 : (bytes + 512 * 1024) / (1024 * 1024);
}
