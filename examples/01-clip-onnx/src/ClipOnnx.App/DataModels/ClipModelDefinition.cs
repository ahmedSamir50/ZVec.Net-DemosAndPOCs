namespace ClipOnnx.App.DataModels;

public sealed record ClipModelDefinition(
    string Id,
    string DisplayName,
    string HfRepo,
    int EmbeddingDim,
    string AccuracyTier,
    string LatencyExpectation,
    string DownloadSizeNote,
    string VramNote,
    string WhenToPick,
    IReadOnlyDictionary<string, string> RemoteFiles);
