namespace ProductSearch.Core.Services;

using ProductSearch.Core.Models;
using ProductSearch.Core.Storage;
using ProductSearch.Shared.Dtos;

public enum IngestState
{
    Idle,
    Downloading,
    Encoding,
    UpsertingZVec,
    CommittingSql,
    Optimizing,
    Completed,
    Failed
}

public sealed class IngestProgressStatus
{
    private const int MaxLogEvents = 80;

    private readonly object _gate = new();
    private readonly Queue<IngestLogEventDto> _events = new();
    private IngestState _state = IngestState.Idle;
    private bool _isRunning;
    private string _message = "Idle — start a patch ingest from the UI.";
    private string? _error;
    private int _catalogOffset;
    private int _catalogTotal;
    private int _patchSize;
    private int _patchIndex;
    private int _encodedThisPatch;
    private int _zvecUpserted;
    private int _sqlCommitted;
    private long _downloadBytesReceived;
    private long? _downloadBytesTotal;

    public IngestProgressDto Snapshot()
    {
        lock (_gate)
        {
            return new IngestProgressDto
            {
                Status = _state.ToString(),
                IsRunning = _isRunning,
                Message = _message,
                PatchSize = _patchSize,
                PatchIndex = _patchIndex,
                Encoded = _encodedThisPatch,
                ZVecUpserted = _zvecUpserted,
                SqlCommitted = _sqlCommitted,
                IngestOffset = _catalogOffset,
                CatalogTotal = _catalogTotal,
                DownloadBytesReceived = _downloadBytesReceived,
                DownloadBytesTotal = _downloadBytesTotal,
                ErrorMessage = _error,
                Events = _events.ToList()
            };
        }
    }

    public void AppendEvent(string level, string stage, string message, long? elapsedMs = null)
    {
        lock (_gate)
        {
            _events.Enqueue(new IngestLogEventDto
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Stage = stage,
                Message = message,
                ElapsedMs = elapsedMs
            });

            while (_events.Count > MaxLogEvents)
                _events.Dequeue();
        }
    }

    public void ClearEvents()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    public IngestProgressDto SnapshotHydrated(IndexStamp stamp, int catalogTotal)
    {
        var dto = Snapshot();
        if (dto.IsRunning)
            return dto;

        if (dto.IngestOffset <= 0 && stamp.IngestOffset > 0)
            dto.IngestOffset = stamp.IngestOffset;

        if (catalogTotal > 0)
            dto.CatalogTotal = catalogTotal;

        if (dto.IngestOffset > 0
            && dto.CatalogTotal > 0
            && dto.IngestOffset < dto.CatalogTotal
            && dto.Status is nameof(IngestState.Idle) or nameof(IngestState.Completed)
            && (dto.Status == nameof(IngestState.Idle)
                || dto.Message.StartsWith("Idle", StringComparison.Ordinal)
                || dto.Message.StartsWith("Optimized", StringComparison.Ordinal)))
        {
            dto.Message = $"Catalog offset {dto.IngestOffset}/{dto.CatalogTotal} — start another patch to continue.";
        }

        return dto;
    }

    public void SetRunning(bool isRunning)
    {
        lock (_gate)
        {
            _isRunning = isRunning;
        }
    }

    public void ResetForPatch(int patchSize, int patchIndex)
    {
        lock (_gate)
        {
            _isRunning = true;
            _state = IngestState.Downloading;
            _message = $"Starting patch {patchIndex} (size {patchSize})…";
            _error = null;
            _patchSize = patchSize;
            _patchIndex = patchIndex;
            _encodedThisPatch = 0;
            _zvecUpserted = 0;
            _sqlCommitted = 0;
            _downloadBytesReceived = 0;
            _downloadBytesTotal = null;
            _events.Clear();
        }
    }

    public void SetDownloading(string message, string? phaseDetail, long received, long? total)
    {
        lock (_gate)
        {
            _state = IngestState.Downloading;
            _message = message;
            _downloadBytesReceived = received;
            _downloadBytesTotal = total;
        }
    }

    public void SetEncoding(string message, int offset, int total, int encoded)
    {
        lock (_gate)
        {
            _state = IngestState.Encoding;
            _message = message;
            _catalogOffset = offset;
            _catalogTotal = total;
            _encodedThisPatch = encoded;
        }
    }

    public void SetUpsertingZVec(string message, int zvecUpserted)
    {
        lock (_gate)
        {
            _state = IngestState.UpsertingZVec;
            _message = message;
            _zvecUpserted = zvecUpserted;
        }
    }

    public void SetCommittingSql(string message, int sqlCommitted)
    {
        lock (_gate)
        {
            _state = IngestState.CommittingSql;
            _message = message;
            _sqlCommitted = sqlCommitted;
        }
    }

    public void SetOptimizing(string message)
    {
        lock (_gate)
        {
            _state = IngestState.Optimizing;
            _message = message;
        }
    }

    public void SetCompleted(string message, int offset, int total, int encoded, int zvec, int sql, long elapsedMs)
    {
        lock (_gate)
        {
            _isRunning = false;
            _state = IngestState.Completed;
            _message = message;
            _error = null;
            _catalogOffset = offset;
            _catalogTotal = total;
            _encodedThisPatch = encoded;
            _zvecUpserted = zvec;
            _sqlCommitted = sql;
        }
    }

    public void SetFailed(string message, string error)
    {
        lock (_gate)
        {
            _isRunning = false;
            _state = IngestState.Failed;
            _message = message;
            _error = error;
        }
    }

    public void SetIdle(string message)
    {
        lock (_gate)
        {
            _isRunning = false;
            _state = IngestState.Idle;
            _message = message;
            _error = null;
        }
    }
}
