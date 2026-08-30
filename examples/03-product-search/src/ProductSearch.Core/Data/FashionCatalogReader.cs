using System.Globalization;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;

namespace ProductSearch.Core.Data;

public sealed class FashionCatalogReader
{
    private readonly ProductSearchOptions _options;

    public FashionCatalogReader(ProductSearchOptions options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<CatalogProduct>> ReadAllAsync(CancellationToken ct = default)
    {
        var path = _options.CatalogCsvPath();
        if (!File.Exists(path))
            throw new FileNotFoundException("Catalog data.csv not found. Extract the in-repo pack first.", path);

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
        var current = "";
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(current);
                current = "";
                continue;
            }

            current += ch;
        }

        result.Add(current);
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
