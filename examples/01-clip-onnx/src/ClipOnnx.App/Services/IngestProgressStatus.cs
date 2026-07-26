namespace ClipOnnx.App.Services;

public enum IngestState
{
    Idle,
    Downloading,
    Extracting,
    Embedding,
    Completed,
    Failed
}

/// <summary>
/// Live Flickr8k ingest progress (download → extract → upsert), polled via GET /api/status.
/// </summary>
public sealed class IngestProgressStatus
{
    private readonly object _gate = new();
    private IngestState _state = IngestState.Idle;
    private string _message = "Idle — click Ingest to download (first run) or embed more images.";
    private string? _error;
    private string? _phaseDetail;
    private long _bytesReceived;
    private long? _bytesTotal;
    private int _manifestOffset;
    private int _manifestTotal;
    private int _embeddedThisRun;
    private int _skippedThisRun;
    private int _targetThisRun;
    private int _zipsDownloadedThisRun;
    private long _elapsedMs;

    public IngestProgressSnapshot Snapshot()
    {
        lock (_gate)
        {
            double? downloadPct = _bytesTotal is > 0
                ? Math.Round(100.0 * _bytesReceived / _bytesTotal.Value, 1)
                : null;

            double? embedPct = null;
            if (_targetThisRun > 0)
                embedPct = Math.Round(100.0 * (_embeddedThisRun + _skippedThisRun) / _targetThisRun, 1);
            else if (_manifestTotal > 0)
                embedPct = Math.Round(100.0 * _manifestOffset / _manifestTotal, 1);

            return new IngestProgressSnapshot(
                _state.ToString(),
                _message,
                _error,
                _phaseDetail,
                _bytesReceived,
                _bytesTotal,
                downloadPct,
                _manifestOffset,
                _manifestTotal,
                _embeddedThisRun,
                _skippedThisRun,
                _targetThisRun,
                embedPct,
                _zipsDownloadedThisRun,
                _elapsedMs,
                IsActive(_state));
        }
    }

    public void ResetForRun(int targetThisRun)
    {
        lock (_gate)
        {
            _state = IngestState.Downloading;
            _message = "Starting ingest…";
            _error = null;
            _phaseDetail = null;
            _bytesReceived = 0;
            _bytesTotal = null;
            _embeddedThisRun = 0;
            _skippedThisRun = 0;
            _targetThisRun = targetThisRun;
            _zipsDownloadedThisRun = 0;
            _elapsedMs = 0;
        }
    }

    public void SetDownloading(string message, string? phaseDetail, long received, long? total)
    {
        lock (_gate)
        {
            _state = IngestState.Downloading;
            _message = message;
            _phaseDetail = phaseDetail;
            _bytesReceived = received;
            _bytesTotal = total;
        }
    }

    public void SetExtracting(string message, string? phaseDetail = null)
    {
        lock (_gate)
        {
            _state = IngestState.Extracting;
            _message = message;
            _phaseDetail = phaseDetail;
            _bytesReceived = 0;
            _bytesTotal = null;
        }
    }

    public void SetEmbedding(
        string message,
        int offset,
        int total,
        int embeddedThisRun,
        int skippedThisRun,
        int targetThisRun)
    {
        lock (_gate)
        {
            _state = IngestState.Embedding;
            _message = message;
            _phaseDetail = null;
            _manifestOffset = offset;
            _manifestTotal = total;
            _embeddedThisRun = embeddedThisRun;
            _skippedThisRun = skippedThisRun;
            _targetThisRun = targetThisRun;
        }
    }

    public void IncrementZipDownloaded()
    {
        lock (_gate) _zipsDownloadedThisRun++;
    }

    public void SetCompleted(string message, int offset, int total, int embedded, int skipped, long elapsedMs)
    {
        lock (_gate)
        {
            _state = IngestState.Completed;
            _message = message;
            _error = null;
            _phaseDetail = null;
            _manifestOffset = offset;
            _manifestTotal = total;
            _embeddedThisRun = embedded;
            _skippedThisRun = skipped;
            _elapsedMs = elapsedMs;
        }
    }

    public void SetFailed(string message, string error)
    {
        lock (_gate)
        {
            _state = IngestState.Failed;
            _message = message;
            _error = error;
        }
    }

    public void SetIdle(string message)
    {
        lock (_gate)
        {
            _state = IngestState.Idle;
            _message = message;
            _error = null;
            _phaseDetail = null;
        }
    }

    private static bool IsActive(IngestState state)
        => state is IngestState.Downloading or IngestState.Extracting or IngestState.Embedding;
}

public sealed record IngestProgressSnapshot(
    string State,
    string Message,
    string? Error,
    string? PhaseDetail,
    long BytesReceived,
    long? BytesTotal,
    double? DownloadPercent,
    int ManifestOffset,
    int ManifestTotal,
    int EmbeddedThisRun,
    int SkippedThisRun,
    int TargetThisRun,
    double? EmbedPercent,
    int ZipsDownloadedThisRun,
    long ElapsedMs,
    bool Active);
