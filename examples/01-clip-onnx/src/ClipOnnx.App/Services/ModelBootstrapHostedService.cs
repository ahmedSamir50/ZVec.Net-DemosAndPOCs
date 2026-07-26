using ClipOnnx.App.Encoding;
using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Services;

/// <summary>
/// Startup glue (not CLIP math): ensure vision/text ONNX + vocab/merges exist under ModelsDir
/// (download from HF if missing), then call <see cref="IClipEncoder.InitializeFromDisk"/>.
/// </summary>
public sealed class ModelBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ModelBootstrapStatus _status;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ClipOnnxOptions> _options;
    private readonly ILogger<ModelBootstrapHostedService> _logger;
    private Task? _work;

    public ModelBootstrapHostedService(
        IServiceProvider services,
        ModelBootstrapStatus status,
        IHttpClientFactory httpClientFactory,
        IOptions<ClipOnnxOptions> options,
        ILogger<ModelBootstrapHostedService> logger)
    {
        _services = services;
        _status = status;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _work = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_work is null) return;
        try { await _work.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var opt = _options.Value;
        var modelsDir = Path.GetFullPath(opt.ModelsDir);
        Directory.CreateDirectory(modelsDir);
        _status.SetModelsDir(modelsDir);
        _status.InitFiles(opt.RequiredModelFiles);
        _status.SetState(ModelBootstrapState.Checking, $"Checking models in {modelsDir}");

        try
        {
            foreach (var file in opt.RequiredModelFiles)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(modelsDir, file);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    _status.UpdateFile(file, ModelFileStatus.Present, new FileInfo(path).Length, new FileInfo(path).Length);
                    _logger.LogInformation("Model present: {File}", path);
                    continue;
                }

                if (!opt.AutoDownloadModels)
                {
                    _status.UpdateFile(file, ModelFileStatus.Failed);
                    throw new FileNotFoundException(
                        $"Missing model file and AutoDownloadModels=false: {path}");
                }

                _status.SetState(ModelBootstrapState.Downloading, $"Downloading {file}…");
                _status.UpdateFile(file, ModelFileStatus.Downloading);
                var url = opt.ModelDownloadUrlTemplate.Replace("{file}", file, StringComparison.Ordinal);
                await DownloadFileAsync(url, path, file, ct);
                _status.UpdateFile(file, ModelFileStatus.Done, new FileInfo(path).Length, new FileInfo(path).Length);
                _logger.LogInformation("Downloaded model: {File}", path);
            }

            _status.SetState(ModelBootstrapState.Loading, "Loading ONNX sessions…");
            var encoder = _services.GetRequiredService<IClipEncoder>();
            if (encoder is not ClipEncoder clip)
                throw new InvalidOperationException("IClipEncoder must be ClipEncoder for InitializeFromDisk.");

            clip.InitializeFromDisk();
            _status.SetState(ModelBootstrapState.Ready, $"Models ready in {modelsDir}");
            _logger.LogInformation("CLIP encoder ready from {Dir}", modelsDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model bootstrap failed");
            _status.SetState(ModelBootstrapState.Failed, "Model bootstrap failed", ex.Message);
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, string fileName, CancellationToken ct)
    {
        var partial = destPath + ".partial";
        if (File.Exists(partial))
        {
            try { File.Delete(partial); } catch { /* best effort */ }
        }

        var client = _httpClientFactory.CreateClient("models");
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
                _status.UpdateFile(fileName, ModelFileStatus.Downloading, received, total);
                lastReport = DateTime.UtcNow;
            }
        }

        await output.FlushAsync(ct);
        output.Close();

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);
        _status.UpdateFile(fileName, ModelFileStatus.Done, received, total ?? received);
    }
}
