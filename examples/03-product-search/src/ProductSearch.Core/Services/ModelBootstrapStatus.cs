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
    double? Percent);

public sealed class ModelBootstrapStatus
{
    private readonly object _gate = new();
    private ModelBootstrapState _state = ModelBootstrapState.Checking;
    private string _modelsDir = "";
    private string _message = "Starting…";
    private string? _error;
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
            if (error is not null)
                _error = error;
            if (state == ModelBootstrapState.Ready)
                _error = null;
        }
    }

    public void InitFiles(IEnumerable<string> names)
    {
        lock (_gate)
        {
            _files = names.Select(n => new ModelFileProgress(n, ModelFileStatus.Pending, 0, null, null)).ToList();
        }
    }

    public void UpdateFile(string name, ModelFileStatus status, long received = 0, long? total = null)
    {
        lock (_gate)
        {
            var idx = _files.FindIndex(f => f.Name == name);
            if (idx < 0) return;
            double? pct = total is > 0 ? Math.Round(100.0 * received / total.Value, 1) : null;
            _files[idx] = new ModelFileProgress(name, status, received, total, pct);
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
    IReadOnlyList<ModelFileProgress> Files,
    double? OverallPercent);
