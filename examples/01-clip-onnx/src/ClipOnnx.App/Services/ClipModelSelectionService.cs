using ClipOnnx.App.Encoding;
using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using ClipOnnx.App.Storage;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Services;

public sealed record ModelExpectationsDto(
    string Id,
    string DisplayName,
    int EmbeddingDim,
    string AccuracyTier,
    string LatencyExpectation,
    string DownloadSizeNote,
    string VramNote,
    string WhenToPick);

public sealed record ModelSelectResult(
    bool Ok,
    string? Error,
    string ActiveModelId,
    int EmbeddingDim,
    bool ModelMismatch,
    string? MismatchMessage,
    ModelExpectationsDto Expectations);

public interface IClipModelSelectionService
{
    ClipModelDefinition ActiveDefinition { get; }
    IReadOnlyList<ModelExpectationsDto> ListExpectations();
    ModelExpectationsDto ExpectationsFor(string modelId);
    /// <summary>Download if needed, reload ONNX, switch gallery path, persist ActiveModelId.</summary>
    Task<ModelSelectResult> SelectAsync(string modelId, CancellationToken ct = default);
    string ModelsDirectoryFor(string modelId);
}

/// <summary>
/// UI/API model picker: ensure per-model ONNX files exist, hot-swap <see cref="IClipEncoder"/>,
/// point <see cref="GalleryStore"/> at the matching collection path, report stamp mismatch.
/// </summary>
public sealed class ClipModelSelectionService : IClipModelSelectionService
{
    private readonly IClipEncoder _encoder;
    private readonly GalleryStore _gallery;
    private readonly IGalleryStampStore _stamp;
    private readonly ModelBootstrapStatus _bootstrap;
    private readonly IHttpClientFactory _http;
    private readonly IOptions<ClipOnnxOptions> _options;
    private readonly ILogger<ClipModelSelectionService> _logger;
    private readonly object _gate = new();
    private string _activeId;

    public ClipModelSelectionService(
        IClipEncoder encoder,
        GalleryStore gallery,
        IGalleryStampStore stamp,
        ModelBootstrapStatus bootstrap,
        IHttpClientFactory http,
        IOptions<ClipOnnxOptions> options,
        ILogger<ClipModelSelectionService> logger)
    {
        _encoder = encoder;
        _gallery = gallery;
        _stamp = stamp;
        _bootstrap = bootstrap;
        _http = http;
        _options = options;
        _logger = logger;
        _activeId = string.IsNullOrWhiteSpace(options.Value.ActiveModelId)
            ? ClipModelCatalog.DefaultModelId
            : options.Value.ActiveModelId;
    }

    public ClipModelDefinition ActiveDefinition => ClipModelCatalog.Get(_activeId);

    public IReadOnlyList<ModelExpectationsDto> ListExpectations()
        => ClipModelCatalog.All.Select(ToDto).ToList();

    public ModelExpectationsDto ExpectationsFor(string modelId)
        => ToDto(ClipModelCatalog.Get(modelId));

    public string ModelsDirectoryFor(string modelId)
        => Path.GetFullPath(Path.Combine(_options.Value.ModelsDir, modelId));

    public async Task<ModelSelectResult> SelectAsync(string modelId, CancellationToken ct = default)
    {
        ClipModelDefinition def;
        try
        {
            def = ClipModelCatalog.Get(modelId);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        lock (_gate)
        {
            // serialize selects
        }

        try
        {
            var dir = ModelsDirectoryFor(def.Id);
            Directory.CreateDirectory(dir);
            _bootstrap.SetModelsDir(dir);
            _bootstrap.InitFiles(_options.Value.RequiredModelFiles);
            _bootstrap.SetState(ModelBootstrapState.Checking, $"Checking {def.DisplayName} in {dir}");

            await EnsureFilesAsync(def, dir, ct).ConfigureAwait(false);

            _bootstrap.SetState(ModelBootstrapState.Loading, $"Loading {def.DisplayName} ONNX…");
            if (_encoder is not ClipEncoder clip)
                throw new InvalidOperationException("IClipEncoder must be ClipEncoder.");

            clip.InitializeFromDisk(dir, def);
            _gallery.SwitchToModel(def);
            _activeId = def.Id;
            _options.Value.ActiveModelId = def.Id;

            _bootstrap.SetState(ModelBootstrapState.Ready, $"{def.DisplayName} ready ({def.EmbeddingDim}-d)");
            var stamp = _stamp.Load();
            var mismatch = _stamp.IsMismatch(def, stamp);
            return new ModelSelectResult(
                true,
                null,
                def.Id,
                def.EmbeddingDim,
                mismatch,
                _stamp.MismatchMessage(def, stamp),
                ToDto(def));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model select failed for {ModelId}", modelId);
            _bootstrap.SetState(ModelBootstrapState.Failed, "Model select failed", ex.Message);
            return Fail(ex.Message);
        }
    }

    private ModelSelectResult Fail(string error)
    {
        var def = ActiveDefinition;
        var stamp = _stamp.Load();
        return new ModelSelectResult(
            false,
            error,
            def.Id,
            def.EmbeddingDim,
            _stamp.IsMismatch(def, stamp),
            _stamp.MismatchMessage(def, stamp),
            ToDto(def));
    }

    private async Task EnsureFilesAsync(ClipModelDefinition def, string dir, CancellationToken ct)
    {
        var opt = _options.Value;
        foreach (var localName in opt.RequiredModelFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!def.RemoteFiles.TryGetValue(localName, out var remote))
                throw new InvalidOperationException($"Catalog missing remote path for {localName} on {def.Id}");

            var path = Path.Combine(dir, localName);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                _bootstrap.UpdateFile(localName, ModelFileStatus.Present, new FileInfo(path).Length, new FileInfo(path).Length);
                continue;
            }

            if (!opt.AutoDownloadModels)
            {
                _bootstrap.UpdateFile(localName, ModelFileStatus.Failed);
                throw new FileNotFoundException($"Missing model file and AutoDownloadModels=false: {path}");
            }

            _bootstrap.SetState(ModelBootstrapState.Downloading, $"Downloading {def.Id}/{localName}…");
            _bootstrap.UpdateFile(localName, ModelFileStatus.Downloading);
            var url = ClipModelCatalog.DownloadUrl(def, remote);
            await DownloadFileAsync(url, path, localName, ct).ConfigureAwait(false);
            _bootstrap.UpdateFile(localName, ModelFileStatus.Done, new FileInfo(path).Length, new FileInfo(path).Length);
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, string fileName, CancellationToken ct)
    {
        var partial = destPath + ".partial";
        if (File.Exists(partial))
        {
            try { File.Delete(partial); } catch { /* best effort */ }
        }

        var client = _http.CreateClient("models");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);

        var buffer = new byte[80 * 1024];
        long received = 0;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250 || received == total)
            {
                _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, received, total);
                lastReport = DateTime.UtcNow;
            }
        }

        await output.FlushAsync(ct);
        output.Close();

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);
        _bootstrap.UpdateFile(fileName, ModelFileStatus.Done, received, total ?? received);
    }

    private static ModelExpectationsDto ToDto(ClipModelDefinition m)
        => new(m.Id, m.DisplayName, m.EmbeddingDim, m.AccuracyTier, m.LatencyExpectation,
            m.DownloadSizeNote, m.VramNote, m.WhenToPick);
}
