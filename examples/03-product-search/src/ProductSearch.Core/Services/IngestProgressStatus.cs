namespace ProductSearch.Core.Services;

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
    private readonly object _gate = new();
    private IngestState _state = IngestState.Idle;
    private string _message = "Idle — start a patch ingest from the UI.";
    private string? _error;
    private int _catalogOffset;
    private int _catalogTotal;
    private int _patchSize;
    private int _patchIndex;
    private int _encodedThisPatch;
    private int _zvecUpserted;
    private int _sqlCommitted;

    public IngestProgressDto Snapshot()
    {
        lock (_gate)
        {
            return new IngestProgressDto
            {
                Status = _state.ToString(),
                PatchSize = _patchSize,
                PatchIndex = _patchIndex,
                Encoded = _encodedThisPatch,
                ZVecUpserted = _zvecUpserted,
                SqlCommitted = _sqlCommitted,
                IngestOffset = _catalogOffset,
                ErrorMessage = _error
            };
        }
    }

    public void ResetForPatch(int patchSize, int patchIndex)
    {
        lock (_gate)
        {
            _state = IngestState.Downloading;
            _message = $"Starting patch {patchIndex} (size {patchSize})…";
            _error = null;
            _patchSize = patchSize;
            _patchIndex = patchIndex;
            _encodedThisPatch = 0;
            _zvecUpserted = 0;
            _sqlCommitted = 0;
        }
    }

    public void SetDownloading(string message, string? phaseDetail, long received, long? total)
    {
        lock (_gate)
        {
            _state = IngestState.Downloading;
            _message = message;
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
        }
    }
}
