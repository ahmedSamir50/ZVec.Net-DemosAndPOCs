using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;

namespace ProductSearch.Core.Data;

public sealed class FashionCatalogReader
{
    private readonly ProductSearchOptions _options;
    private readonly ILogger<FashionCatalogReader>? _logger;
    private readonly object _cacheGate = new();
    private IReadOnlyList<CatalogProduct>? _cache;
    private long _cacheWriteTicks;
    private string? _cachePath;

    public FashionCatalogReader(ProductSearchOptions options, ILogger<FashionCatalogReader>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> GetCatalogTotalAsync(CancellationToken ct = default)
    {
        var catalog = await GetOrLoadCatalogAsync(ct).ConfigureAwait(false);
        return catalog.Count;
    }

    public async Task<IReadOnlyList<CatalogProduct>> ReadSliceAsync(int offset, int count, CancellationToken ct = default)
    {
        if (count <= 0)
            return [];

        var catalog = await GetOrLoadCatalogAsync(ct).ConfigureAwait(false);
        if (offset >= catalog.Count)
            return [];

        var take = Math.Min(count, catalog.Count - offset);
        var slice = new List<CatalogProduct>(take);
        for (var i = 0; i < take; i++)
            slice.Add(catalog[offset + i]);
        return slice;
    }

    public Task<IReadOnlyList<CatalogProduct>> ReadAllAsync(CancellationToken ct = default)
        => GetOrLoadCatalogAsync(ct);

    private async Task<IReadOnlyList<CatalogProduct>> GetOrLoadCatalogAsync(CancellationToken ct)
    {
        var path = _options.CatalogCsvPath();
        if (!File.Exists(path))
            throw new FileNotFoundException("Catalog data.csv not found. Extract the in-repo pack first.", path);

        var writeTicks = new FileInfo(path).LastWriteTimeUtc.Ticks;
        lock (_cacheGate)
        {
            if (_cache is not null
                && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase)
                && _cacheWriteTicks == writeTicks)
                return _cache;
        }

        var loaded = await ParseCatalogFileAsync(path, ct).ConfigureAwait(false);
        lock (_cacheGate)
        {
            _cache = loaded;
            _cachePath = path;
            _cacheWriteTicks = writeTicks;
        }

        return loaded;
    }

    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cache = null;
            _cachePath = null;
            _cacheWriteTicks = 0;
        }
    }

    private async Task<IReadOnlyList<CatalogProduct>> ParseCatalogFileAsync(string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        if (lines.Length == 0)
            return [];

        var headers = SplitCsvLine(lines[0]);
        var index = BuildHeaderIndex(headers);
        var rows = new List<CatalogProduct>(Math.Max(0, lines.Length - 1));

        for (var i = 1; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var fields = SplitCsvLine(lines[i]);
            if (fields.Count == 0)
                continue;

            var product = TryParseRow(fields, index);
            if (product is not null)
                rows.Add(product);
        }

        _logger?.LogDebug("Parsed {Count} catalog rows from {Path}", rows.Count, path);
        return rows;
    }

    private CatalogProduct? TryParseRow(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> index)
    {
        if (index.ContainsKey("image"))
            return ParseDataCsvRow(fields, index);

        return ParseStylesCsvRow(fields, index);
    }

    private CatalogProduct? ParseDataCsvRow(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> index)
    {
        var imageFile = Get(fields, index, "image");
        if (string.IsNullOrWhiteSpace(imageFile))
            return null;

        var catalogId = Path.GetFileNameWithoutExtension(imageFile.Trim());
        if (string.IsNullOrWhiteSpace(catalogId))
            return null;

        var category = Get(fields, index, "category");
        var product = new CatalogProduct
        {
            CatalogId = catalogId,
            ProductDisplayName = Get(fields, index, "display name"),
            Description = Get(fields, index, "description"),
            MasterCategory = category,
            SubCategory = category,
            ArticleType = category,
            ImageRelPath = Path.Combine(_options.ImagesSubdir, $"{catalogId}.jpg")
        };
        product.ConcatenatedText = BuildConcatenatedText(product);
        return product;
    }

    private CatalogProduct? ParseStylesCsvRow(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> index)
    {
        var catalogId = Get(fields, index, "id");
        if (string.IsNullOrWhiteSpace(catalogId))
            return null;

        var product = new CatalogProduct
        {
            CatalogId = catalogId.Trim(),
            Gender = Get(fields, index, "gender"),
            MasterCategory = Get(fields, index, "masterCategory"),
            SubCategory = Get(fields, index, "subCategory"),
            ArticleType = Get(fields, index, "articleType"),
            BaseColour = Get(fields, index, "baseColour"),
            Season = Get(fields, index, "season"),
            Year = int.TryParse(Get(fields, index, "year"), out var y) ? y : 0,
            Usage = Get(fields, index, "usage"),
            ProductDisplayName = Get(fields, index, "productDisplayName"),
            ImageRelPath = Path.Combine(_options.ImagesSubdir, $"{catalogId.Trim()}.jpg")
        };
        product.ConcatenatedText = BuildConcatenatedText(product);
        return product;
    }

    private static Dictionary<string, int> BuildHeaderIndex(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
            map[headers[i].Trim()] = i;
        return map;
    }

    private static string Get(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> index, string name)
    {
        if (!index.TryGetValue(name, out var pos) || pos < 0 || pos >= fields.Count)
            return "";
        return fields[pos].Trim();
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result;
    }

    public static string BuildConcatenatedText(CatalogProduct product)
    {
        var parts = new[]
        {
            product.ProductDisplayName,
            product.ArticleType,
            product.SubCategory,
            product.MasterCategory,
            product.Description,
            product.BaseColour,
            product.Season,
            product.Usage,
            product.Gender,
            product.Year > 0 ? product.Year.ToString(CultureInfo.InvariantCulture) : null
        };

        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
