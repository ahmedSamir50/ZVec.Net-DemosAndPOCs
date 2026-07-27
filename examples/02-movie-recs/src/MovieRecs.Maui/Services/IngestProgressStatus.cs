namespace MovieRecs.Maui.Services;

/// <summary>Thread-safe ingest phase/progress for the Home progress bar (polled while ingest runs).</summary>
public sealed class IngestProgressStatus
{
    private readonly object _gate = new();

    public bool IsRunning { get; private set; }
    public int Done { get; private set; }
    public int Total { get; private set; }
    public string Phase { get; private set; } = "Idle";
    public string? Error { get; private set; }

    public double Fraction
    {
        get
        {
            lock (_gate)
                return Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            IsRunning = false;
            Done = 0;
            Total = 0;
            Phase = "Idle";
            Error = null;
        }
    }

    public void Begin(string phase, int total)
    {
        lock (_gate)
        {
            IsRunning = true;
            Phase = phase;
            Done = 0;
            Total = Math.Max(0, total);
            Error = null;
        }
    }

    public void Report(int done, string? phase = null)
    {
        lock (_gate)
        {
            Done = done;
            if (phase is not null)
                Phase = phase;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            IsRunning = false;
            Phase = "Ready";
            Done = Total;
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            IsRunning = false;
            Error = error;
            Phase = "Error";
        }
    }

    public IngestSnapshot Snapshot()
    {
        lock (_gate)
            return new IngestSnapshot(IsRunning, Done, Total, Phase, Error, Fraction);
    }
}

public sealed record IngestSnapshot(
    bool IsRunning,
    int Done,
    int Total,
    string Phase,
    string? Error,
    double Fraction);
