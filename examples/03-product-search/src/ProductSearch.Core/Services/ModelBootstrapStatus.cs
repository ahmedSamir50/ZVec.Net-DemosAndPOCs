using ProductSearch.Core.Models;

namespace ProductSearch.Core.Services;

public enum ModelBootstrapState
{
    Checking,
    Downloading,
    Loading,
    Ready,
    Failed
}

public enum ModelFileStatus
{
    Pending,
    Present,
    Downloading,
    Done,
    Failed
}

public sealed record ModelFileProgress(
    string Name,
    ModelFileStatus Status,
    long BytesReceived,
    long? BytesTotal,
    double? Percent,
    bool OnDisk,
    string FullPath);

public sealed class ModelBootstrapStatus
{
    private readonly object _gate = new();
    private ModelBootstrapState _state = ModelBootstrapState.Checking;
    private string _modelsDir = "";
    private string _message = "Starting…";
    private string? _error;
    private string? _errorDetail;
    private List<ModelFileProgress> _files = [];

    public ModelBootstrapSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ModelBootstrapSnapshot(
                _state.ToString(),
                _modelsDir,
                _message,
                _error,
                _errorDetail,
                _files.ToList(),
                OverallPercent(_files));
        }
    }

    public void SetModelsDir(string path)
    {
        lock (_gate) _modelsDir = path;
    }

    public void SetState(ModelBootstrapState state, string message, string? error = null)
    {
        lock (_gate)
        {
            _state = state;
            _message = message;
            if (state is ModelBootstrapState.Checking
                or ModelBootstrapState.Downloading
                or ModelBootstrapState.Loading
                or ModelBootstrapState.Ready)
            {
                _error = null;
                _errorDetail = null;
            }
            else if (error is not null)
            {
                _error = error;
            }
        }
    }

    public void SetFailure(string message, string error, string? errorDetail = null)
    {
        lock (_gate)
        {
            _state = ModelBootstrapState.Failed;
            _message = message;
            _error = error;
            _errorDetail = errorDetail;
        }
    }

    public void InitFiles(IEnumerable<string> names, string modelsDir)
    {
        lock (_gate)
        {
            _modelsDir = modelsDir;
            _files = [.. names.Select(n => new ModelFileProgress(
                n,
                ModelFileStatus.Pending,
                0,
                null,
                null,
                false,
                Path.Combine(modelsDir, n)))];
        }
    }

    public void UpdateFile(string name, ModelFileStatus status, long received = 0, long? total = null)
    {
        lock (_gate)
        {
            var idx = _files.FindIndex(f => f.Name == name);
            if (idx < 0) return;
            double? pct = total is > 0 ? Math.Round(100.0 * received / total.Value, 1) : null;
            var fullPath = _files[idx].FullPath;
            var onDisk = File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;
            _files[idx] = new ModelFileProgress(name, status, received, total, pct, onDisk, fullPath);
        }
    }

    public void SyncFileStatusFromDisk(string modelsDir, SigLipModelDefinition model)
    {
        lock (_gate)
        {
            _modelsDir = modelsDir;
            for (var i = 0; i < _files.Count; i++)
            {
                var file = _files[i];
                var path = Path.Combine(modelsDir, file.Name);
                if (!File.Exists(path))
                {
                    _files[i] = file with
                    {
                        Status = ModelFileStatus.Failed,
                        OnDisk = false,
                        FullPath = path,
                        Percent = null
                    };
                    continue;
                }

                var length = new FileInfo(path).Length;
                var expectedOk = !SigLipModelCatalog.TryGetExpectedBytes(model, file.Name, out var expected)
                    || length == expected;
                var status = expectedOk
                    ? file.Status is ModelFileStatus.Downloading or ModelFileStatus.Pending
                        ? ModelFileStatus.Present
                        : file.Status
                    : ModelFileStatus.Failed;

                _files[i] = file with
                {
                    Status = status,
                    OnDisk = expectedOk && length > 0,
                    FullPath = path,
                    BytesReceived = length,
                    BytesTotal = SigLipModelCatalog.TryGetExpectedBytes(model, file.Name, out var bytes)
                        ? bytes
                        : file.BytesTotal,
                    Percent = SigLipModelCatalog.TryGetExpectedBytes(model, file.Name, out var exp) && exp > 0
                        ? Math.Round(100.0 * length / exp, 1)
                        : file.Percent
                };
            }
        }
    }

    private static double? OverallPercent(IReadOnlyList<ModelFileProgress> files)
    {
        if (files.Count == 0) return null;
        var known = files.Where(f => f.BytesTotal is > 0).ToList();
        if (known.Count == 0)
        {
            var done = files.Count(f => f.Status is ModelFileStatus.Present or ModelFileStatus.Done);
            return Math.Round(100.0 * done / files.Count, 1);
        }

        var sumRecv = known.Sum(f => f.BytesReceived);
        var sumTotal = known.Sum(f => f.BytesTotal!.Value);
        return sumTotal > 0 ? Math.Round(100.0 * sumRecv / sumTotal, 1) : null;
    }
}

public sealed record ModelBootstrapSnapshot(
    string State,
    string ModelsDir,
    string Message,
    string? Error,
    string? ErrorDetail,
    IReadOnlyList<ModelFileProgress> Files,
    double? OverallPercent);
