using Microsoft.Extensions.Logging;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Services;

namespace ProductSearch.Core.Data;

public sealed class FashionDatasetDownloader
{
    private readonly ProductSearchOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IngestProgressStatus _progress;
    private readonly ILogger<FashionDatasetDownloader> _logger;

    public FashionDatasetDownloader(
        ProductSearchOptions options,
        IHttpClientFactory httpClientFactory,
        IngestProgressStatus progress,
        ILogger<FashionDatasetDownloader> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _progress = progress;
        _logger = logger;
    }

    public async Task EnsureStylesCsvAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.CatalogCachePath);
        var dest = _options.CatalogStylesPath();
        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            return;

        _progress.SetDownloading("Downloading styles.csv…", "styles.csv", 0, null);
        await DownloadAsync(_options.StylesCsvUrl, dest, ct).ConfigureAwait(false);
        _logger.LogInformation("Downloaded styles.csv to {Path}", dest);
    }

    public async Task<string> EnsureImageAsync(string catalogId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        var imagesDir = _options.CatalogImagesDirectory();
        Directory.CreateDirectory(imagesDir);

        var localPath = Path.Combine(imagesDir, $"{catalogId.Trim()}.jpg");
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            return localPath;

        var url = _options.ImageUrlTemplate.Replace("{id}", catalogId.Trim(), StringComparison.Ordinal);
        _progress.SetDownloading($"Downloading image {catalogId}.jpg…", catalogId, 0, null);
        await DownloadAsync(url, localPath, ct).ConfigureAwait(false);
        return localPath;
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var partial = destPath + ".partial";
        if (File.Exists(partial))
        {
            try { File.Delete(partial); } catch { /* best effort */ }
        }

        var client = _httpClientFactory.CreateClient("fashion");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);

        var buffer = new byte[80 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            _progress.SetDownloading("Downloading…", Path.GetFileName(destPath), received, total);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Close();

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);
    }
}
