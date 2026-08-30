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
            _bootstrap.InitFiles(def.RequiredModelFiles);
            _bootstrap.SetState(ModelBootstrapState.Checking, $"Checking {def.DisplayName} in {dir}");

            await EnsureFilesAsync(def, dir, ct).ConfigureAwait(false);

            _bootstrap.SetState(ModelBootstrapState.Loading, $"Loading {def.DisplayName} ONNX…");
            if (_encoder is not SigLipEncoder siglip)
                throw new InvalidOperationException("ISigLipEncoder must be SigLipEncoder.");

            try
            {
                siglip.InitializeFromDisk(dir, def);
            }
            catch (Exception loadEx)
            {
                _logger.LogWarning(loadEx, "ONNX load failed — removing model files in {Dir}", dir);
                DeleteInvalidModelFiles(dir, def.RequiredModelFiles);
                throw;
            }
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
        foreach (var localName in def.RequiredModelFiles)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(dir, localName);
            var expectedBytes = await ResolveExpectedBytesAsync(def, localName, ct).ConfigureAwait(false);

            if (File.Exists(path))
            {
                var length = new FileInfo(path).Length;
                if (IsExactModelFileSize(length, expectedBytes))
                {
                    _bootstrap.UpdateFile(localName, ModelFileStatus.Present, length, expectedBytes);
                    continue;
                }

                _logger.LogWarning(
                    "Removing invalid model file {Path}: expected {Expected} bytes, found {Actual} bytes",
                    path, expectedBytes, length);
                TryDelete(path);
                TryDelete(path + ".partial");
            }

            if (!opt.AutoDownloadModels)
            {
                _bootstrap.UpdateFile(localName, ModelFileStatus.Failed);
                throw new FileNotFoundException($"Missing model file and AutoDownloadModels=false: {path}");
            }

            _bootstrap.SetState(ModelBootstrapState.Downloading, $"Downloading {def.Id}/{localName}…");
            _bootstrap.UpdateFile(localName, ModelFileStatus.Downloading, 0, expectedBytes);
            var url = SigLipModelCatalog.DownloadUrl(def, localName);
            await DownloadFileAsync(url, path, localName, expectedBytes, ct).ConfigureAwait(false);
            var finalLength = new FileInfo(path).Length;
            if (!IsExactModelFileSize(finalLength, expectedBytes))
            {
                TryDelete(path);
                throw new InvalidDataException(
                    $"Downloaded {localName} size mismatch: expected {expectedBytes} bytes, got {finalLength} bytes.");
            }

            _bootstrap.UpdateFile(localName, ModelFileStatus.Done, finalLength, expectedBytes);
        }
    }

    private async Task<long> ResolveExpectedBytesAsync(
        SigLipModelDefinition def,
        string localName,
        CancellationToken ct)
    {
        if (SigLipModelCatalog.TryGetExpectedBytes(def, localName, out var catalogBytes))
            return catalogBytes;

        var url = SigLipModelCatalog.DownloadUrl(def, localName);
        var remoteBytes = await ProbeRemoteContentLengthAsync(url, ct).ConfigureAwait(false);
        if (remoteBytes is null or <= 0)
            throw new InvalidOperationException($"Could not resolve exact byte size for {def.Id}/{localName}.");

        _logger.LogInformation(
            "Using remote Content-Length {Bytes} for {ModelId}/{File} (not cataloged)",
            remoteBytes, def.Id, localName);
        return remoteBytes.Value;
    }

    private async Task<long?> ProbeRemoteContentLengthAsync(string url, CancellationToken ct)
    {
        var client = _http.CreateClient("models");
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is > 0)
            return response.Content.Headers.ContentLength;

        if (!response.IsSuccessStatusCode)
            return null;

        using var get = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return get.Content.Headers.ContentLength;
    }

    private static bool IsExactModelFileSize(long actualBytes, long expectedBytes)
        => actualBytes > 0 && actualBytes == expectedBytes;

    private static void DeleteInvalidModelFiles(string dir, IReadOnlyList<string> files)
    {
        foreach (var localName in files)
        {
            var path = Path.Combine(dir, localName);
            TryDelete(path);
            TryDelete(path + ".partial");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string fileName,
        long expectedBytes,
        CancellationToken ct)
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
        if (total is > 0 && total != expectedBytes)
        {
            throw new InvalidDataException(
                $"Remote {fileName} Content-Length is {total} bytes, expected {expectedBytes} bytes.");
        }

        var bytesTotal = total ?? expectedBytes;
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
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250 || received == bytesTotal)
            {
                _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, received, bytesTotal);
                lastReport = DateTime.UtcNow;
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Close();

        if (received != expectedBytes)
        {
            TryDelete(partial);
            throw new InvalidDataException(
                $"Incomplete download for {fileName}: received {received} of {expectedBytes} bytes.");
        }

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);
        _bootstrap.UpdateFile(fileName, ModelFileStatus.Done, received, expectedBytes);
    }

    private static ModelExpectationsDto ToDto(SigLipModelDefinition m)
        => new(m.Id, m.DisplayName, m.EmbeddingDim, m.ImageSize, m.AccuracyTier, m.LatencyExpectation,
            m.DownloadSizeNote, m.WhenToPick);
}
