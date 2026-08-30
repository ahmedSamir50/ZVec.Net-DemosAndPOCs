using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Services;

namespace ProductSearch.Core.Data;

/// <summary>
/// Fetches the ashraq/fashion-product-images-small catalog via Hugging Face
/// datasets-server <c>/rows</c> (max 100 rows per request) and writes a local
/// styles.csv plus an id→row_idx map. Images are pulled from each row's
/// signed <c>image.src</c> into <c>images/{id}.jpg</c>.
/// </summary>
public sealed class FashionDatasetDownloader
{
    private const int MaxCachedPages = 3;

    private readonly ProductSearchOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IngestProgressStatus _progress;
    private readonly ILogger<FashionDatasetDownloader> _logger;
    private readonly Dictionary<int, HfPage> _pageCache = new();
    private Dictionary<string, int>? _idToRowIdx;

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
        var csvPath = _options.CatalogStylesPath();
        var indexPath = _options.CatalogRowIndexPath();

        if (File.Exists(csvPath) && new FileInfo(csvPath).Length > 0
            && File.Exists(indexPath) && new FileInfo(indexPath).Length > 0)
        {
            return;
        }

        var pageSize = Math.Clamp(_options.HuggingFaceRowsPageSize, 1, 100);
        var csvPartial = csvPath + ".partial";
        var indexPartial = indexPath + ".partial";
        DeleteIfExists(csvPartial);
        DeleteIfExists(indexPartial);

        _progress.SetDownloading("Fetching catalog page 1 from Hugging Face datasets-server…", "styles.csv", 0, null);

        await using var csv = new StreamWriter(csvPartial, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using var index = new StreamWriter(indexPartial, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await csv.WriteLineAsync("id,gender,masterCategory,subCategory,articleType,baseColour,season,year,usage,productDisplayName").ConfigureAwait(false);

        var offset = 0;
        var total = (int?)null;
        var written = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchPageAsync(offset, pageSize, ct).ConfigureAwait(false);
            if (page.Rows.Count == 0)
                break;

            total ??= page.NumRowsTotal;
            var totalPages = total is int t && t > 0
                ? (int)Math.Ceiling(t / (double)pageSize)
                : offset / pageSize + 1;
            var pageNum = offset / pageSize + 1;
            _progress.SetDownloading(
                $"Fetching catalog page {pageNum}/{totalPages} ({written + page.Rows.Count:N0} rows)…",
                "styles.csv",
                written + page.Rows.Count,
                total);

            foreach (var row in page.Rows)
            {
                await csv.WriteLineAsync(FormatCsvLine(row)).ConfigureAwait(false);
                await index.WriteLineAsync($"{row.Id}\t{row.RowIdx.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            }

            written += page.Rows.Count;
            CachePage(page);

            if (page.Rows.Count < pageSize)
                break;
            if (total is int known && offset + pageSize >= known)
                break;

            offset += pageSize;
        }

        await csv.FlushAsync(ct).ConfigureAwait(false);
        await index.FlushAsync(ct).ConfigureAwait(false);

        csv.Close();
        index.Close();

        ReplaceFile(csvPartial, csvPath);
        ReplaceFile(indexPartial, indexPath);
        _idToRowIdx = null;

        _logger.LogInformation("Wrote {Count} catalog rows to {Path}", written, csvPath);
    }

    public async Task<string> EnsureImageAsync(string catalogId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        var id = catalogId.Trim();
        var imagesDir = _options.CatalogImagesDirectory();
        Directory.CreateDirectory(imagesDir);

        var localPath = Path.Combine(imagesDir, $"{id}.jpg");
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            return localPath;

        await EnsureStylesCsvAsync(ct).ConfigureAwait(false);
        var rowIdx = await LookupRowIndexAsync(id, ct).ConfigureAwait(false);
        var pageSize = Math.Clamp(_options.HuggingFaceRowsPageSize, 1, 100);
        var pageOffset = rowIdx / pageSize * pageSize;
        var page = await GetCachedOrFetchPageAsync(pageOffset, pageSize, ct).ConfigureAwait(false);
        var row = page.Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
            ?? page.Rows.FirstOrDefault(r => r.RowIdx == rowIdx);

        if (row is null || string.IsNullOrWhiteSpace(row.ImageSrc))
            throw new InvalidOperationException($"No image URL for catalog id {id} (HF row {rowIdx}).");

        _progress.SetDownloading($"Downloading image {id}.jpg…", id, 0, null);
        await DownloadAsync(row.ImageSrc, localPath, ct).ConfigureAwait(false);
        return localPath;
    }

    private async Task<int> LookupRowIndexAsync(string catalogId, CancellationToken ct)
    {
        var map = await LoadIndexAsync(ct).ConfigureAwait(false);
        if (!map.TryGetValue(catalogId, out var rowIdx))
            throw new KeyNotFoundException($"Catalog id {catalogId} is not in the Hugging Face row index.");
        return rowIdx;
    }

    private async Task<Dictionary<string, int>> LoadIndexAsync(CancellationToken ct)
    {
        if (_idToRowIdx is not null)
            return _idToRowIdx;

        var path = _options.CatalogRowIndexPath();
        if (!File.Exists(path))
            throw new FileNotFoundException("Hugging Face row index not found. Download the catalog first.", path);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                continue;
            var id = line[..tab];
            if (int.TryParse(line[(tab + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowIdx))
                map[id] = rowIdx;
        }

        _idToRowIdx = map;
        return map;
    }

    private async Task<HfPage> GetCachedOrFetchPageAsync(int offset, int pageSize, CancellationToken ct)
    {
        if (_pageCache.TryGetValue(offset, out var cached))
            return cached;
        var page = await FetchPageAsync(offset, pageSize, ct).ConfigureAwait(false);
        CachePage(page);
        return page;
    }

    private void CachePage(HfPage page)
    {
        _pageCache[page.Offset] = page;
        while (_pageCache.Count > MaxCachedPages)
        {
            var oldest = _pageCache.Keys.Min();
            _pageCache.Remove(oldest);
        }
    }

    private async Task<HfPage> FetchPageAsync(int offset, int length, CancellationToken ct)
    {
        var url = _options.HuggingFaceRowsUrl
            .Replace("{offset}", offset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{length}", length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var json = await GetStringWithRetryAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var total = root.TryGetProperty("num_rows_total", out var totalEl) && totalEl.TryGetInt32(out var t)
            ? t
            : (int?)null;

        var rows = new List<HfRow>();
        if (root.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rowsEl.EnumerateArray())
            {
                var rowIdx = item.TryGetProperty("row_idx", out var idxEl) && idxEl.TryGetInt32(out var idx)
                    ? idx
                    : offset + rows.Count;
                if (!item.TryGetProperty("row", out var rowEl) || rowEl.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadString(rowEl, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var imageSrc = "";
                if (rowEl.TryGetProperty("image", out var imageEl) && imageEl.ValueKind == JsonValueKind.Object
                    && imageEl.TryGetProperty("src", out var srcEl))
                {
                    imageSrc = srcEl.GetString() ?? "";
                }

                rows.Add(new HfRow(
                    Id: id,
                    RowIdx: rowIdx,
                    Gender: ReadString(rowEl, "gender"),
                    MasterCategory: ReadString(rowEl, "masterCategory"),
                    SubCategory: ReadString(rowEl, "subCategory"),
                    ArticleType: ReadString(rowEl, "articleType"),
                    BaseColour: ReadString(rowEl, "baseColour"),
                    Season: ReadString(rowEl, "season"),
                    Year: ReadYear(rowEl),
                    Usage: ReadString(rowEl, "usage"),
                    ProductDisplayName: ReadString(rowEl, "productDisplayName"),
                    ImageSrc: imageSrc));
            }
        }

        return new HfPage(offset, rows, total);
    }

    private async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("fashion");
        const int maxAttempts = 5;
        HttpRequestException? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                last = new HttpRequestException($"Hugging Face datasets-server returned {(int)response.StatusCode} for {url}.");
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning("Rate limited on {Url}; retry {Attempt}/{Max} after {Delay}s", url, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var snippet = body.Length > 240 ? body[..240] : body;
                throw new HttpRequestException(
                    $"Hugging Face datasets-server returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {url}. {snippet}");
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        throw last ?? new HttpRequestException($"Failed to fetch {url}.");
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var partial = destPath + ".partial";
        DeleteIfExists(partial);

        var client = _httpClientFactory.CreateClient("fashion");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snippet = body.Length > 240 ? body[..240] : body;
            throw new HttpRequestException(
                $"Image download returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {url}. {snippet}");
        }

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
        ReplaceFile(partial, destPath);
    }

    private static string FormatCsvLine(HfRow row)
        => string.Join(',',
            Csv(row.Id),
            Csv(row.Gender),
            Csv(row.MasterCategory),
            Csv(row.SubCategory),
            Csv(row.ArticleType),
            Csv(row.BaseColour),
            Csv(row.Season),
            row.Year.ToString(CultureInfo.InvariantCulture),
            Csv(row.Usage),
            Csv(row.ProductDisplayName));

    private static string Csv(string value)
    {
        if (value.IndexOfAny(['"', ',', '\n', '\r']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => el.ToString()
        };
    }

    private static int ReadYear(JsonElement obj)
    {
        if (!obj.TryGetProperty("year", out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return 0;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out var i))
                return i;
            if (el.TryGetDouble(out var d))
                return (int)d;
        }
        return int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
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

    private sealed record HfRow(
        string Id,
        int RowIdx,
        string Gender,
        string MasterCategory,
        string SubCategory,
        string ArticleType,
        string BaseColour,
        string Season,
        int Year,
        string Usage,
        string ProductDisplayName,
        string ImageSrc);

    private sealed record HfPage(int Offset, IReadOnlyList<HfRow> Rows, int? NumRowsTotal);
}
