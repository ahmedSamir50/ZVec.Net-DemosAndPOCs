using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Services;

namespace ProductSearch.Core.Data;

/// <summary>
/// Lazy-extracts the in-repo <c>fashion-10k.zip</c> pack: <c>data.csv</c> once on first ingest,
/// then individual <c>images/{id}.jpg</c> entries only when a patch needs them.
/// </summary>
public sealed class FashionDatasetDownloader : IDisposable
{
    private readonly ProductSearchOptions _options;
    private readonly IngestProgressStatus _progress;
    private readonly ILogger<FashionDatasetDownloader> _logger;
    private readonly object _zipGate = new();
    private readonly SemaphoreSlim _zipExtract = new(1, 1);
    private FileStream? _packStream;
    private ZipArchive? _pack;

    public FashionDatasetDownloader(
        ProductSearchOptions options,
        IngestProgressStatus progress,
        ILogger<FashionDatasetDownloader> logger)
    {
        _options = options;
        _progress = progress;
        _logger = logger;
    }

    public async Task EnsureStylesCsvAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.CatalogCachePath);
        var csvPath = _options.CatalogCsvPath();
        if (File.Exists(csvPath) && new FileInfo(csvPath).Length > 0)
            return;

        _progress.SetDownloading("Extracting catalog data.csv from in-repo pack…", "data.csv", 0, null);
        _logger.LogInformation("Opening catalog pack at {PackPath}", _options.CatalogPackZip);
        await ExtractZipEntryAsync("data.csv", csvPath, ct).ConfigureAwait(false);
        _logger.LogInformation("Extracted catalog CSV to {Path}", csvPath);
    }

    /// <summary>
    /// Returns the local JPEG path, extracting from the pack if needed. Null when the pack has no image for this id.
    /// </summary>
    public async Task<string?> TryEnsureImageAsync(string catalogId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        var id = catalogId.Trim();
        var imagesDir = _options.CatalogImagesDirectory();
        Directory.CreateDirectory(imagesDir);

        var localPath = Path.Combine(imagesDir, $"{id}.jpg");
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            return localPath;

        await EnsureStylesCsvAsync(ct).ConfigureAwait(false);

        var zipEntry = $"{_options.ImagesSubdir}/{id}.jpg";
        if (!PackEntryExists(zipEntry))
            return null;

        _progress.SetDownloading($"Extracting image {id}.jpg…", id, 0, null);
        await ExtractZipEntryAsync(zipEntry, localPath, ct).ConfigureAwait(false);
        _logger.LogDebug("Extracted image {CatalogId} to {Path}", id, localPath);
        return localPath;
    }

    /// <summary>Prefetch JPEGs for a chunk — cache hits in parallel, zip extracts serialized.</summary>
    public async Task<IReadOnlyDictionary<string, string>> PrefetchImagesAsync(
        IEnumerable<string> catalogIds,
        CancellationToken ct = default)
    {
        var imagesDir = _options.CatalogImagesDirectory();
        Directory.CreateDirectory(imagesDir);
        await EnsureStylesCsvAsync(ct).ConfigureAwait(false);

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var toExtract = new List<string>();

        foreach (var raw in catalogIds)
        {
            ct.ThrowIfCancellationRequested();
            var id = raw.Trim();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var localPath = Path.Combine(imagesDir, $"{id}.jpg");
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                results[id] = localPath;
                continue;
            }

            toExtract.Add(id);
        }

        if (toExtract.Count == 0)
            return results;

        await Parallel.ForEachAsync(
            toExtract,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (id, token) =>
            {
                var path = await TryEnsureImageAsync(id, token).ConfigureAwait(false);
                if (path is not null)
                {
                    lock (results)
                        results[id] = path;
                }
            }).ConfigureAwait(false);

        return results;
    }

    private bool PackEntryExists(string entryName)
    {
        lock (_zipGate)
        {
            var pack = EnsurePackUnlocked();
            return pack.GetEntry(entryName) is not null;
        }
    }

    private ZipArchive EnsurePackUnlocked()
    {
        if (_pack is not null)
            return _pack;

        var path = _options.CatalogPackZip;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"In-repo catalog pack not found at {path}. Expected fashion-10k.zip next to wow-queries.json.",
                path);
        }

        _packStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _pack = new ZipArchive(_packStream, ZipArchiveMode.Read, leaveOpen: false);
        return _pack;
    }

    private async Task ExtractZipEntryAsync(string entryName, string destPath, CancellationToken ct)
    {
        await _zipExtract.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ZipArchiveEntry entry;
            lock (_zipGate)
            {
                var pack = EnsurePackUnlocked();
                entry = pack.GetEntry(entryName)
                        ?? throw new FileNotFoundException(
                            $"Entry '{entryName}' not found in catalog pack {_options.CatalogPackZip}.");
            }

            var total = entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var partial = destPath + ".partial";
            DeleteIfExists(partial);

            await using var input = entry.Open();
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);

            var buffer = new byte[80 * 1024];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                _progress.SetDownloading($"Extracting {entryName}…", Path.GetFileName(destPath), received, total);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Close();

            ReplaceFile(partial, destPath);
        }
        finally
        {
            _zipExtract.Release();
        }
    }

    private static void ReplaceFile(string partial, string dest)
    {
        if (File.Exists(dest))
            File.Delete(dest);
        File.Move(partial, dest);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        lock (_zipGate)
        {
            _pack?.Dispose();
            _pack = null;
            _packStream?.Dispose();
            _packStream = null;
        }
        _zipExtract.Dispose();
    }
}
