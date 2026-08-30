using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Models;
using ProductSearch.Core.Storage;

namespace ProductSearch.Core.Services;

public sealed record ModelExpectationsDto(
    string Id,
    string DisplayName,
    int EmbeddingDim,
    int ImageSize,
    string AccuracyTier,
    string LatencyExpectation,
    string DownloadSizeNote,
    string WhenToPick);

public sealed record ModelSelectResult(
    bool Ok,
    string? Error,
    string ActiveModelId,
    int EmbeddingDim,
    bool StampMismatch,
    string? MismatchMessage,
    ModelExpectationsDto Expectations);

public interface ISigLipModelSelectionService
{
    SigLipModelDefinition ActiveDefinition { get; }
    IReadOnlyList<ModelExpectationsDto> ListExpectations();
    ModelExpectationsDto ExpectationsFor(string modelId);
    Task<ModelSelectResult> SelectAsync(string modelId, CancellationToken ct = default);
    string ModelsDirectoryFor(string modelId);
}

public sealed class SigLipModelSelectionService : ISigLipModelSelectionService
{
    private readonly ISigLipEncoder _encoder;
    private readonly DualCollectionHolder _collections;
    private readonly IIndexStampStore _stamp;
    private readonly ModelBootstrapStatus _bootstrap;
    private readonly IHttpClientFactory _http;
    private readonly IOptions<ProductSearchOptions> _options;
    private readonly ILogger<SigLipModelSelectionService> _logger;
    private readonly object _gate = new();
    private string _activeId;

    public SigLipModelSelectionService(
        ISigLipEncoder encoder,
        DualCollectionHolder collections,
        IIndexStampStore stamp,
        ModelBootstrapStatus bootstrap,
        IHttpClientFactory http,
        IOptions<ProductSearchOptions> options,
        ILogger<SigLipModelSelectionService> logger)
    {
        _encoder = encoder;
        _collections = collections;
        _stamp = stamp;
        _bootstrap = bootstrap;
        _http = http;
        _options = options;
        _logger = logger;
        _activeId = string.IsNullOrWhiteSpace(options.Value.ActiveModelId)
            ? SigLipModelCatalog.DefaultModelId
            : options.Value.ActiveModelId;
    }

    public SigLipModelDefinition ActiveDefinition => SigLipModelCatalog.Get(_activeId);

    public IReadOnlyList<ModelExpectationsDto> ListExpectations()
        => SigLipModelCatalog.All.Select(ToDto).ToList();

    public ModelExpectationsDto ExpectationsFor(string modelId)
        => ToDto(SigLipModelCatalog.Get(modelId));

    public string ModelsDirectoryFor(string modelId)
        => _options.Value.ModelsDirectoryFor(modelId);

    public async Task<ModelSelectResult> SelectAsync(string modelId, CancellationToken ct = default)
    {
        SigLipModelDefinition def;
        try
        {
            def = SigLipModelCatalog.Get(modelId);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        lock (_gate) { /* serialize */ }

        try
        {
            var dir = ModelsDirectoryFor(def.Id);
            Directory.CreateDirectory(dir);
            _bootstrap.SetModelsDir(dir);
            _bootstrap.InitFiles(_options.Value.RequiredModelFiles);
            _bootstrap.SetState(ModelBootstrapState.Checking, $"Checking {def.DisplayName} in {dir}");

            await EnsureFilesAsync(def, dir, ct).ConfigureAwait(false);

            _bootstrap.SetState(ModelBootstrapState.Loading, $"Loading {def.DisplayName} ONNX…");
            if (_encoder is not SigLipEncoder siglip)
                throw new InvalidOperationException("ISigLipEncoder must be SigLipEncoder.");

            siglip.InitializeFromDisk(dir, def);
            _collections.SwitchToModel(def);
            _collections.EnsureIndexes();
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

    private async Task EnsureFilesAsync(SigLipModelDefinition def, string dir, CancellationToken ct)
    {
        var opt = _options.Value;
        foreach (var localName in opt.RequiredModelFiles)
        {
            ct.ThrowIfCancellationRequested();
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
            var url = SigLipModelCatalog.DownloadUrl(def, localName);
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
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);

        var buffer = new byte[80 * 1024];
        long received = 0;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250 || received == total)
            {
                _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, received, total);
                lastReport = DateTime.UtcNow;
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Close();

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);
        _bootstrap.UpdateFile(fileName, ModelFileStatus.Done, received, total ?? received);
    }

    private static ModelExpectationsDto ToDto(SigLipModelDefinition m)
        => new(m.Id, m.DisplayName, m.EmbeddingDim, m.ImageSize, m.AccuracyTier, m.LatencyExpectation,
            m.DownloadSizeNote, m.WhenToPick);
}
