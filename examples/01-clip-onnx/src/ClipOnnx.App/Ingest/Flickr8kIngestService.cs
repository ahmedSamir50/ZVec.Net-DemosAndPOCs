using System.IO.Compression;
using ClipOnnx.App.Encoding;
using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using ClipOnnx.App.Services;
using ClipOnnx.App.Storage;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Ingest;

public sealed record IngestStartResult(bool Started, int MaxImages, string? Error);
public sealed record IngestResetResult(bool Reset, string? Error);

public interface IFlickr8kIngestService
{
    /// <summary>
    /// Start background ingest (download if needed → extract → embed up to maxImages).
    /// Returns false if a run is already active.
    /// </summary>
    IngestStartResult TryStartIngest(int maxImages);

    /// <summary>
    /// Wipe ZVec gallery + reset manifest offset to 0 (keeps downloaded images).
    /// Fails if ingest is running.
    /// </summary>
    IngestResetResult TryResetIndex();

    /// <summary>
    /// Merge flat upsert buffer into HNSW for the active gallery (no re-embed).
    /// </summary>
    IngestOptimizeResult TryOptimize();
}

public sealed record IngestOptimizeResult(bool Ok, string? Error);

/// <summary>
/// Downloads Flickr8k once (full zip when images/ is empty), then indexes vision-only CLIP embeddings.
/// maxImages limits encode+upsert per run (resume via state/flickr8k.json) — not dataset download size.
/// Progress is published on <see cref="IngestProgressStatus"/> for GET /api/status.
/// </summary>
public sealed class Flickr8kIngestService : IFlickr8kIngestService
{
    private const int StatePersistEvery = 10;

    private readonly GalleryStore _gallery;
    private readonly IClipEncoder _encoder;
    private readonly IClipModelSelectionService _models;
    private readonly IGalleryStampStore _stamp;
    private readonly ClipOnnxOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IngestProgressStatus _status;
    private readonly ILogger<Flickr8kIngestService> _logger;
    private readonly object _startGate = new();
    private int _running; // 0 = idle, 1 = running

    public Flickr8kIngestService(
        GalleryStore gallery,
        IClipEncoder encoder,
        IClipModelSelectionService models,
        IGalleryStampStore stamp,
        IOptions<ClipOnnxOptions> options,
        IHttpClientFactory httpClientFactory,
        IngestProgressStatus status,
        ILogger<Flickr8kIngestService> logger)
    {
        _gallery = gallery;
        _encoder = encoder;
        _models = models;
        _stamp = stamp;
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

        var active = _models.ActiveDefinition;
        if (_stamp.IsMismatch(active))
            return new IngestStartResult(false, maxImages, _stamp.MismatchMessage(active) + " Reset index first.");

        lock (_startGate)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return new IngestStartResult(false, maxImages, "Ingest already running.");

            _status.ResetForRun(maxImages);
            _ = Task.Run(() => RunIngestAsync(maxImages));
            return new IngestStartResult(true, maxImages, null);
        }
    }

    public IngestResetResult TryResetIndex()
    {
        lock (_startGate)
        {
            if (Volatile.Read(ref _running) != 0)
                return new IngestResetResult(false, "Ingest already running.");

            try
            {
                var active = _models.ActiveDefinition;
                _gallery.SwitchToModel(active);
                _gallery.RecreateEmpty();
                // Clear stamp so the next ingest owns this model (offset 0, no mismatch).
                _stamp.Save(new GalleryStamp(
                    Offset: 0,
                    ModelId: active.Id,
                    EmbeddingDim: active.EmbeddingDim,
                    EncodePipelineVersion: ClipModelCatalog.EncodePipelineVersion));
                _status.SetIdle($"Index reset for {active.DisplayName} ({active.EmbeddingDim}-d). Click Ingest to re-embed.");
                _logger.LogInformation("Gallery index reset at {Path} for {ModelId}", _gallery.CollectionPath, active.Id);
                return new IngestResetResult(true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Index reset failed");
                return new IngestResetResult(false, ex.Message);
            }
        }
    }

    public IngestOptimizeResult TryOptimize()
    {
        lock (_startGate)
        {
            if (Volatile.Read(ref _running) != 0)
                return new IngestOptimizeResult(false, "Ingest already running.");

            try
            {
                // Upserts stage in a flat buffer; Optimize merges into HNSW for production-quality ANN.
                _gallery.Optimize();
                _status.SetIdle($"Optimized HNSW index for {_models.ActiveDefinition.DisplayName}.");
                _logger.LogInformation("Gallery Optimize() completed for {ModelId}", _gallery.ModelId);
                return new IngestOptimizeResult(true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Optimize failed");
                return new IngestOptimizeResult(false, ex.Message);
            }
        }
    }

    private async Task RunIngestAsync(int maxImages)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var active = _models.ActiveDefinition;
            var flickrRoot = Path.GetFullPath(Path.Combine(_options.DataRoot, "flickr8k"));
            var imagesDir = Path.Combine(flickrRoot, "images");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(Path.GetDirectoryName(_stamp.StatePath)!);

            await EnsureManifestAndDatasetAsync(flickrRoot, imagesDir, CancellationToken.None);

            var manifest = await LoadManifestAsync(flickrRoot, CancellationToken.None);
            var state = _stamp.Load();
            // If stamp matches active but offset was reset, continue; else start clean for this model.
            if (!_stamp.IsMismatch(active, state)
                && string.Equals(state.ModelId, active.Id, StringComparison.OrdinalIgnoreCase))
            {
                /* resume */
            }
            else if (state.Offset > 0 && _stamp.IsMismatch(active, state))
            {
                throw new InvalidOperationException(_stamp.MismatchMessage(active, state));
            }

            var offset = Math.Clamp(state.Offset, 0, manifest.Count);
            if (!string.Equals(state.ModelId, active.Id, StringComparison.OrdinalIgnoreCase) && state.Offset == 0)
            {
                state = new GalleryStamp(0, active.Id, active.EmbeddingDim, ClipModelCatalog.EncodePipelineVersion);
            }

            var remaining = Math.Max(0, manifest.Count - offset);
            var target = Math.Min(maxImages, remaining);

            _status.SetEmbedding(
                target == 0
                    ? "Nothing left to embed at current offset."
                    : $"Embedding up to {target} with {active.DisplayName} from offset {offset}…",
                offset, manifest.Count, 0, 0, target);

            var embedded = 0;
            var skipped = 0;
            var processed = 0;

            while (processed < target && offset < manifest.Count)
            {
                var fileName = manifest[offset];
                var id = Path.GetFileNameWithoutExtension(fileName);
                var localPath = Path.Combine(imagesDir, fileName);

                if (_gallery.Exists(id))
                {
                    skipped++;
                }
                else if (!File.Exists(localPath))
                {
                    _logger.LogWarning("Manifest image missing on disk: {File}", fileName);
                }
                else
                {
                    // Vision embed only (dim = active model); captions never enter the ZVec index.
                    var embedding = _encoder.EncodeImage(localPath);
                    await _gallery.UpsertAsync(id, localPath, embedding, CancellationToken.None);
                    embedded++;
                }

                offset++;
                processed++;
                state = new GalleryStamp(offset, active.Id, active.EmbeddingDim, ClipModelCatalog.EncodePipelineVersion);
                if (processed % StatePersistEvery == 0 || processed == target)
                    _stamp.Save(state);

                _status.SetEmbedding(
                    $"Embedding {processed}/{target} this run · gallery offset {offset}/{manifest.Count} · {active.Id}",
                    offset, manifest.Count, embedded, skipped, target);
            }

            _stamp.Save(new GalleryStamp(offset, active.Id, active.EmbeddingDim, ClipModelCatalog.EncodePipelineVersion));

            // Upserts stage in a flat buffer; Optimize merges into HNSW for production-quality ANN.
            if (embedded > 0)
            {
                _status.SetEmbedding(
                    $"Optimizing index ({active.Id})…",
                    offset, manifest.Count, embedded, skipped, target);
                _gallery.Optimize();
            }

            sw.Stop();
            var msg = target == 0
                ? $"Caught up — offset {offset}/{manifest.Count} ({active.Id})."
                : $"Ingest complete — embedded {embedded}, skipped {skipped}, offset {offset}/{manifest.Count} ({active.Id}).";
            _status.SetCompleted(msg, offset, manifest.Count, embedded, skipped, sw.ElapsedMilliseconds);
            _logger.LogInformation(
                "Ingest finished: model={Model} embedded={Embedded} skipped={Skipped} offset={Offset}/{Total} in {Ms}ms",
                active.Id, embedded, skipped, offset, manifest.Count, sw.ElapsedMilliseconds);
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
            await EnsureValidZipAsync(
                _options.FlickrTextZipUrl,
                textZip,
                "Flickr8k_text.zip",
                "Downloading Flickr8k text zip (manifest)…",
                ct);

            _status.SetExtracting("Extracting Flickr8k text zip…", "Flickr8k_text.zip");
            ZipFile.ExtractToDirectory(textZip, flickrRoot, overwriteFiles: true);
        }

        // If images folder empty, download + extract image zip (one-time full archive).
        if (!Directory.EnumerateFiles(imagesDir, "*.jpg").Any())
        {
            _logger.LogInformation("Downloading Flickr8k images zip (large, one-time full download)…");
            var imgZip = Path.Combine(flickrRoot, "Flickr8k_Dataset.zip");
            await EnsureValidZipAsync(
                _options.FlickrImagesZipUrl,
                imgZip,
                "Flickr8k_Dataset.zip",
                "Downloading Flickr8k images zip (one-time full dataset)…",
                ct);

            _status.SetExtracting("Extracting Flickr8k images zip (this can take a few minutes)…", "Flickr8k_Dataset.zip");
            try
            {
                ZipFile.ExtractToDirectory(imgZip, flickrRoot, overwriteFiles: true);
            }
            catch (InvalidDataException)
            {
                _logger.LogWarning("Extract failed for {Zip} — deleting corrupt archive and re-downloading once.", imgZip);
                TryDelete(imgZip);
                _status.SetDownloading("Corrupt zip detected — re-downloading…", "Flickr8k_Dataset.zip", 0, null);
                await DownloadAsync(
                    _options.FlickrImagesZipUrl,
                    imgZip,
                    "Flickr8k_Dataset.zip",
                    "Corrupt zip detected — re-downloading Flickr8k images zip…",
                    ct,
                    force: true);
                _status.IncrementZipDownloaded();
                EnsureZipReadable(imgZip);
                _status.SetExtracting("Extracting Flickr8k images zip (retry)…", "Flickr8k_Dataset.zip");
                ZipFile.ExtractToDirectory(imgZip, flickrRoot, overwriteFiles: true);
            }

            _status.SetExtracting("Copying JPEGs into images/…");
            foreach (var jpg in Directory.EnumerateFiles(flickrRoot, "*.jpg", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(imagesDir, Path.GetFileName(jpg));
                if (!File.Exists(dest))
                    File.Copy(jpg, dest);
            }
        }
    }

    /// <summary>
    /// Reuse on-disk zip only if it opens as a valid archive; otherwise download (once more if corrupt).
    /// </summary>
    private async Task EnsureValidZipAsync(
        string url,
        string destPath,
        string displayName,
        string message,
        CancellationToken ct)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 1024)
        {
            if (TryValidateZip(destPath))
            {
                var len = new FileInfo(destPath).Length;
                _status.SetDownloading($"Using existing {displayName}", displayName, len, len);
                return;
            }

            _logger.LogWarning("Corrupt zip on disk ({Path}) — re-downloading.", destPath);
            _status.SetDownloading("Corrupt zip detected — re-downloading…", displayName, 0, null);
            TryDelete(destPath);
            await DownloadAsync(url, destPath, displayName, message, ct, force: true);
            _status.IncrementZipDownloaded();
            EnsureZipReadable(destPath);
            return;
        }

        var downloaded = await DownloadAsync(url, destPath, displayName, message, ct, force: false);
        if (downloaded)
            _status.IncrementZipDownloaded();
        EnsureZipReadable(destPath);
    }

    private static bool TryValidateZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            _ = archive.Entries.Count;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void EnsureZipReadable(string path)
    {
        if (!TryValidateZip(path))
            throw new InvalidDataException(
                $"Zip archive is invalid or truncated: {path}. Delete it and retry ingest.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Returns true if bytes were downloaded this call; false if skipped (already on disk).</summary>
    private async Task<bool> DownloadAsync(
        string url,
        string destPath,
        string displayName,
        string message,
        CancellationToken ct,
        bool force = false)
    {
        if (!force && File.Exists(destPath) && new FileInfo(destPath).Length > 1024)
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

        if (total is > 0 && received != total.Value)
        {
            TryDelete(partial);
            throw new IOException(
                $"Download incomplete for {displayName}: received {received} of {total} bytes. Retry ingest.");
        }

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
}
