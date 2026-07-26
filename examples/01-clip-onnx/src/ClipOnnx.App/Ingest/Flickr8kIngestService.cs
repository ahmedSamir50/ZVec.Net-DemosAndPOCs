using System.IO.Compression;
using System.Text.Json;
using ClipOnnx.App.Encoding;
using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using ClipOnnx.App.Services;
using Microsoft.Extensions.Options;
using ZVec.NET;

namespace ClipOnnx.App.Ingest;

public sealed record IngestStartResult(bool Started, int MaxImages, string? Error);

public interface IFlickr8kIngestService
{
    /// <summary>
    /// Start background ingest (download if needed → extract → embed up to maxImages).
    /// Returns false if a run is already active.
    /// </summary>
    IngestStartResult TryStartIngest(int maxImages);
}

/// <summary>
/// Downloads Flickr8k once (full zip when images/ is empty), then indexes vision-only CLIP embeddings.
/// maxImages limits encode+upsert per run (resume via state/flickr8k.json) — not dataset download size.
/// Progress is published on <see cref="IngestProgressStatus"/> for GET /api/status.
/// </summary>
public sealed class Flickr8kIngestService : IFlickr8kIngestService
{
    private readonly IZvecCollection<ImageAsset> _collection;
    private readonly IClipEncoder _encoder;
    private readonly ClipOnnxOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IngestProgressStatus _status;
    private readonly ILogger<Flickr8kIngestService> _logger;
    private readonly object _startGate = new();
    private int _running; // 0 = idle, 1 = running

    public Flickr8kIngestService(
        IZvecCollection<ImageAsset> collection,
        IClipEncoder encoder,
        IOptions<ClipOnnxOptions> options,
        IHttpClientFactory httpClientFactory,
        IngestProgressStatus status,
        ILogger<Flickr8kIngestService> logger)
    {
        _collection = collection;
        _encoder = encoder;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _status = status;
        _logger = logger;
    }

    public IngestStartResult TryStartIngest(int maxImages)
    {
        if (maxImages <= 0)
            maxImages = _options.DefaultBatchSize;

        if (!_encoder.IsReady)
            return new IngestStartResult(false, maxImages, _encoder.NotReadyReason ?? "CLIP encoder is not ready.");

        lock (_startGate)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return new IngestStartResult(false, maxImages, "Ingest already running.");

            _status.ResetForRun(maxImages);
            _ = Task.Run(() => RunIngestAsync(maxImages));
            return new IngestStartResult(true, maxImages, null);
        }
    }

    private async Task RunIngestAsync(int maxImages)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var flickrRoot = Path.GetFullPath(Path.Combine(_options.DataRoot, "flickr8k"));
            var imagesDir = Path.Combine(flickrRoot, "images");
            var statePath = Path.Combine(_options.DataRoot, "state", "flickr8k.json");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

            await EnsureManifestAndDatasetAsync(flickrRoot, imagesDir, CancellationToken.None);

            var manifest = await LoadManifestAsync(flickrRoot, CancellationToken.None);
            var state = await LoadStateAsync(statePath, CancellationToken.None);
            var offset = Math.Clamp(state.Offset, 0, manifest.Count);
            var remaining = Math.Max(0, manifest.Count - offset);
            var target = Math.Min(maxImages, remaining);

            _status.SetEmbedding(
                target == 0
                    ? "Nothing left to embed at current offset."
                    : $"Embedding up to {target} image(s) from offset {offset}…",
                offset, manifest.Count, 0, 0, target);

            var embedded = 0;
            var skipped = 0;
            var processed = 0;

            while (processed < target && offset < manifest.Count)
            {
                var fileName = manifest[offset];
                var id = Path.GetFileNameWithoutExtension(fileName);
                var localPath = Path.Combine(imagesDir, fileName);

                if (_collection.Fetch(id) is not null)
                {
                    skipped++;
                }
                else if (!File.Exists(localPath))
                {
                    _logger.LogWarning("Manifest image missing on disk: {File}", fileName);
                }
                else
                {
                    // Vision embed only (512-d L2); captions never enter the index.
                    var embedding = _encoder.EncodeImage(localPath);
                    await _collection.UpsertAsync(new ImageAsset
                    {
                        Id = id,
                        Path = localPath,
                        Embedding = embedding
                    }, CancellationToken.None);
                    embedded++;
                }

                offset++;
                processed++;
                state = state with { Offset = offset };
                await SaveStateAsync(statePath, state, CancellationToken.None);

                _status.SetEmbedding(
                    $"Embedding {processed}/{target} this run · gallery offset {offset}/{manifest.Count}",
                    offset, manifest.Count, embedded, skipped, target);
            }

            sw.Stop();
            var msg = target == 0
                ? $"Caught up — offset {offset}/{manifest.Count}."
                : $"Ingest complete — embedded {embedded}, skipped {skipped}, offset {offset}/{manifest.Count}.";
            _status.SetCompleted(msg, offset, manifest.Count, embedded, skipped, sw.ElapsedMilliseconds);
            _logger.LogInformation(
                "Ingest finished: embedded={Embedded} skipped={Skipped} offset={Offset}/{Total} in {Ms}ms",
                embedded, skipped, offset, manifest.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingest failed");
            _status.SetFailed("Ingest failed", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task EnsureManifestAndDatasetAsync(string flickrRoot, string imagesDir, CancellationToken ct)
    {
        var manifestPath = Path.Combine(flickrRoot, _options.FlickrManifestFile);
        if (!File.Exists(manifestPath))
        {
            _logger.LogInformation("Downloading Flickr8k text zip…");
            var textZip = Path.Combine(flickrRoot, "Flickr8k_text.zip");
            var downloaded = await DownloadAsync(
                _options.FlickrTextZipUrl,
                textZip,
                "Flickr8k_text.zip",
                "Downloading Flickr8k text zip (manifest)…",
                ct);
            if (downloaded)
                _status.IncrementZipDownloaded();

            _status.SetExtracting("Extracting Flickr8k text zip…", "Flickr8k_text.zip");
            ZipFile.ExtractToDirectory(textZip, flickrRoot, overwriteFiles: true);
        }

        // If images folder empty, download + extract image zip (one-time full archive).
        if (!Directory.EnumerateFiles(imagesDir, "*.jpg").Any())
        {
            _logger.LogInformation("Downloading Flickr8k images zip (large, one-time full download)…");
            var imgZip = Path.Combine(flickrRoot, "Flickr8k_Dataset.zip");
            var downloaded = await DownloadAsync(
                _options.FlickrImagesZipUrl,
                imgZip,
                "Flickr8k_Dataset.zip",
                "Downloading Flickr8k images zip (one-time full dataset)…",
                ct);
            if (downloaded)
                _status.IncrementZipDownloaded();

            _status.SetExtracting("Extracting Flickr8k images zip (this can take a few minutes)…", "Flickr8k_Dataset.zip");
            ZipFile.ExtractToDirectory(imgZip, flickrRoot, overwriteFiles: true);

            _status.SetExtracting("Copying JPEGs into images/…");
            foreach (var jpg in Directory.EnumerateFiles(flickrRoot, "*.jpg", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(imagesDir, Path.GetFileName(jpg));
                if (!File.Exists(dest))
                    File.Copy(jpg, dest);
            }
        }
    }

    /// <summary>Returns true if bytes were downloaded this call; false if skipped (already on disk).</summary>
    private async Task<bool> DownloadAsync(
        string url,
        string destPath,
        string displayName,
        string message,
        CancellationToken ct)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 1024)
        {
            var len = new FileInfo(destPath).Length;
            _status.SetDownloading($"Using existing {displayName}", displayName, len, len);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var partial = destPath + ".partial";
        if (File.Exists(partial))
        {
            try { File.Delete(partial); } catch { /* best effort */ }
        }

        var client = _httpClientFactory.CreateClient("flickr");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        _status.SetDownloading(message, displayName, 0, total);

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
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
                _status.SetDownloading(message, displayName, received, total);
                lastReport = DateTime.UtcNow;
            }
        }

        await output.FlushAsync(ct);
        output.Close();

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(partial, destPath);

        _status.SetDownloading($"Downloaded {displayName}", displayName, received, total ?? received);
        return true;
    }

    private async Task<IReadOnlyList<string>> LoadManifestAsync(string flickrRoot, CancellationToken ct)
    {
        var path = Path.Combine(flickrRoot, _options.FlickrManifestFile);
        if (!File.Exists(path))
        {
            path = Directory.EnumerateFiles(flickrRoot, _options.FlickrManifestFile, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new FileNotFoundException("Flickr manifest not found after download.", _options.FlickrManifestFile);
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        return lines
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<FlickrState> LoadStateAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new FlickrState(0);
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<FlickrState>(fs, cancellationToken: ct) ?? new FlickrState(0);
    }

    private static async Task SaveStateAsync(string path, FlickrState state, CancellationToken ct)
    {
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, state, cancellationToken: ct);
    }

    private sealed record FlickrState(int Offset);
}
