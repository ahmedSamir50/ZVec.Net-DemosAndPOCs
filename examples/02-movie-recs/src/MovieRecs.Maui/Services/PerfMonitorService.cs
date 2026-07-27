using System.Diagnostics;

namespace MovieRecs.Maui.Services;

/// <summary>Snapshot of process CPU % and memory for the edge demo strip.</summary>
public sealed record PerfSnapshot(double CpuPercent, double WorkingSetMb, double ManagedHeapMb);

/// <summary>
/// Samples process CPU and memory for the MAUI edge talk track.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Process.TotalProcessorTime"/> is <b>cumulative</b> — a single reading is not a percentage.
/// We sample two points and compute:
/// <c>cpu% = ΔTotalProcessorTime / (elapsed × ProcessorCount) × 100</c>.
/// </para>
/// Working set = OS-reported process memory; managed heap is GC.GetTotalMemory (secondary).
/// </remarks>
public sealed class PerfMonitorService
{
    private readonly object _gate = new();
    private TimeSpan _lastCpu;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private PerfSnapshot _latest = new(0, 0, 0);

    public PerfSnapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public PerfSnapshot Sample()
    {
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var now = DateTime.UtcNow;
        var cpu = proc.TotalProcessorTime;
        var wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        var heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        double cpuPct = 0;
        lock (_gate)
        {
            if (_lastSampleUtc != DateTime.MinValue)
            {
                var elapsed = (now - _lastSampleUtc).TotalSeconds;
                if (elapsed > 0.05)
                {
                    var cpuDelta = (cpu - _lastCpu).TotalSeconds;
                    cpuPct = Math.Clamp(100.0 * cpuDelta / (elapsed * Environment.ProcessorCount), 0, 100 * Environment.ProcessorCount);
                    // Cap display at 100% of one logical view when multi-core spikes look confusing on a tiny strip.
                    cpuPct = Math.Min(cpuPct, 100);
                }
            }

            _lastCpu = cpu;
            _lastSampleUtc = now;
            _latest = new PerfSnapshot(cpuPct, wsMb, heapMb);
            return _latest;
        }
    }
}
