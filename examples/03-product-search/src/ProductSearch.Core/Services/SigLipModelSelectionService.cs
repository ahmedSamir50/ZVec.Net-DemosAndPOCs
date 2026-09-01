using System.Net;
using System.Net.Http.Headers;
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
    private const int MaxConcurrentFileDownloads = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _activeId;
    private string? _inProgressModelId;

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
        var stampData = stamp.Load();
        if (!string.IsNullOrWhiteSpace(stampData.ModelId))
        {
            _activeId = stampData.ModelId;
        }
        else
        {
            _activeId = string.IsNullOrWhiteSpace(options.Value.ActiveModelId)
                ? SigLipModelCatalog.DefaultModelId
                : options.Value.ActiveModelId;
        }
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

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return Fail(DownloadInProgressMessage(_inProgressModelId));

        _inProgressModelId = def.Id;
        try
        {
            var dir = ModelsDirectoryFor(def.Id);
            Directory.CreateDirectory(dir);
            _bootstrap.SetModelsDir(dir);
            _bootstrap.InitFiles(def.RequiredModelFiles, dir);
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
            var (summary, detail) = BootstrapExceptionFormatter.Format(ex);
            var dir = ModelsDirectoryFor(def.Id);
            _bootstrap.SyncFileStatusFromDisk(dir, def);
            _bootstrap.SetFailure("Model select failed", summary, detail);
            return Fail(summary);
        }
        finally
        {
            _inProgressModelId = null;
            _gate.Release();
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
        var pending = new List<(string LocalName, string Path, long ExpectedBytes)>();

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
            }

            if (!opt.AutoDownloadModels)
            {
                _bootstrap.UpdateFile(localName, ModelFileStatus.Failed);
                throw new FileNotFoundException($"Missing model file and AutoDownloadModels=false: {path}");
            }

            var partialBytes = ExistingPartialLength(path + ".partial");
            _bootstrap.UpdateFile(
                localName,
                ModelFileStatus.Downloading,
                Math.Min(partialBytes, expectedBytes),
                expectedBytes);
            pending.Add((localName, path, expectedBytes));
        }

        if (pending.Count == 0)
            return;

        _bootstrap.SetState(ModelBootstrapState.Downloading, $"Downloading {def.Id}…");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var limiter = new SemaphoreSlim(MaxConcurrentFileDownloads, MaxConcurrentFileDownloads);
        var tasks = pending
            .Select(item => DownloadOneFileAsync(
                def, item.LocalName, item.Path, item.ExpectedBytes, limiter, linked, ct))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
            throw FirstDownloadException(tasks);
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

    private static string DownloadInProgressMessage(string? modelId)
        => string.IsNullOrWhiteSpace(modelId)
            ? "Model download already in progress. Wait for it to finish before selecting another model."
            : $"Model download already in progress ({modelId}). Wait for it to finish before selecting another model.";

    private static IOException PartialFileInUse(string fileName, string partial, Exception inner)
        => new(
            $"Model download already in progress for {fileName}. Wait for it to finish, or stop the API and delete '{partial}'.",
            inner);

    private async Task DownloadOneFileAsync(
        SigLipModelDefinition def,
        string localName,
        string path,
        long expectedBytes,
        SemaphoreSlim limiter,
        CancellationTokenSource linked,
        CancellationToken callerCt)
    {
        await limiter.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var url = SigLipModelCatalog.DownloadUrl(def, localName);
            await DownloadFileAsync(url, path, localName, expectedBytes, linked.Token).ConfigureAwait(false);
            var finalLength = new FileInfo(path).Length;
            if (!IsExactModelFileSize(finalLength, expectedBytes))
            {
                TryDelete(path);
                throw new InvalidDataException(
                    $"Downloaded {localName} size mismatch: expected {expectedBytes} bytes, got {finalLength} bytes.");
            }

            _bootstrap.UpdateFile(localName, ModelFileStatus.Done, finalLength, expectedBytes);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !callerCt.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
            throw;
        }
        finally
        {
            limiter.Release();
        }
    }

    private static Exception FirstDownloadException(IReadOnlyList<Task> tasks)
    {
        Exception? canceled = null;
        foreach (var task in tasks)
        {
            if (task.Exception is null)
                continue;

            foreach (var ex in task.Exception.Flatten().InnerExceptions)
            {
                if (ex is OperationCanceledException)
                {
                    canceled ??= ex;
                    continue;
                }

                return ex;
            }
        }

        return canceled ?? new InvalidOperationException("Model download failed.");
    }

    private static long ExistingPartialLength(string partial)
    {
        try
        {
            return File.Exists(partial) ? new FileInfo(partial).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static FileStream OpenPartialStream(string partial, string fileName, bool append)
    {
        try
        {
            if (append)
            {
                var stream = new FileStream(
                    partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);
                stream.Seek(0, SeekOrigin.End);
                return stream;
            }

            return new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);
        }
        catch (IOException ex)
        {
            throw PartialFileInUse(fileName, partial, ex);
        }
    }

    private static async Task<HttpResponseMessage> GetModelFileAsync(
        HttpClient client,
        string url,
        long resumeFrom,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static void ValidateContentLength(
        HttpResponseMessage response,
        string fileName,
        long expectedBytes,
        long resumeFrom)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is null or <= 0)
            return;

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var remaining = expectedBytes - resumeFrom;
            if (contentLength != remaining)
            {
                throw new InvalidDataException(
                    $"Remote {fileName} Range Content-Length is {contentLength} bytes, expected remaining {remaining} bytes.");
            }

            return;
        }

        if (contentLength != expectedBytes)
        {
            throw new InvalidDataException(
                $"Remote {fileName} Content-Length is {contentLength} bytes, expected {expectedBytes} bytes.");
        }
    }

    private void PromotePartial(string partial, string destPath, string fileName, long expectedBytes)
    {
        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);
        _bootstrap.UpdateFile(fileName, ModelFileStatus.Done, expectedBytes, expectedBytes);
    }

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string fileName,
        long expectedBytes,
        CancellationToken ct)
    {
        var partial = destPath + ".partial";
        var resumeFrom = ExistingPartialLength(partial);
        if (resumeFrom > expectedBytes)
        {
            _logger.LogWarning(
                "Discarding oversized partial {Path}: {Actual} bytes, expected {Expected}",
                partial, resumeFrom, expectedBytes);
            TryDelete(partial);
            resumeFrom = 0;
        }
        else if (resumeFrom == expectedBytes)
        {
            PromotePartial(partial, destPath, fileName, expectedBytes);
            return;
        }

        _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, resumeFrom, expectedBytes);

        var client = _http.CreateClient("models");
        var response = await GetModelFileAsync(client, url, resumeFrom, ct).ConfigureAwait(false);
        try
        {
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                TryDelete(partial);
                resumeFrom = 0;
                _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, 0, expectedBytes);
                response = await GetModelFileAsync(client, url, 0, ct).ConfigureAwait(false);
            }

            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                _logger.LogInformation(
                    "Range not honored for {File}; restarting download from byte 0", fileName);
                TryDelete(partial);
                resumeFrom = 0;
                _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, 0, expectedBytes);
            }

            response.EnsureSuccessStatusCode();
            ValidateContentLength(response, fileName, expectedBytes, resumeFrom);

            var append = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append)
                resumeFrom = 0;

            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = OpenPartialStream(partial, fileName, append);

            var buffer = new byte[80 * 1024];
            long received = resumeFrom;
            int read;
            var lastReport = DateTime.UtcNow;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250 || received == expectedBytes)
                {
                    _bootstrap.UpdateFile(fileName, ModelFileStatus.Downloading, received, expectedBytes);
                    lastReport = DateTime.UtcNow;
                }
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Close();

            if (received != expectedBytes)
            {
                throw new InvalidDataException(
                    $"Incomplete download for {fileName}: received {received} of {expectedBytes} bytes.");
            }

            PromotePartial(partial, destPath, fileName, expectedBytes);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static ModelExpectationsDto ToDto(SigLipModelDefinition m)
        => new(m.Id, m.DisplayName, m.EmbeddingDim, m.ImageSize, m.AccuracyTier, m.LatencyExpectation,
            m.DownloadSizeNote, m.WhenToPick);
}
