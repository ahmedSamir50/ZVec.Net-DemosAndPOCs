using System.Globalization;
using System.IO.Compression;
using System.Text;

const string defaultSource = @"C:\Users\DELL\Downloads\archive.zip";
const string defaultOutput = @"..\..\data\fashion-10k.zip";
const int targetCount = 10_000;
const int minDescriptionChars = 40;
const int minImageBytes = 3 * 1024;
const int randomSeed = 42;

var sourceZip = args.Length > 0 ? args[0] : defaultSource;
var outputZip = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath(defaultOutput, AppContext.BaseDirectory);

if (!File.Exists(sourceZip))
{
    Console.Error.WriteLine($"Source zip not found: {sourceZip}");
    return 1;
}

Console.WriteLine($"Source: {sourceZip}");
Console.WriteLine($"Output: {outputZip}");

using var source = ZipFile.OpenRead(sourceZip);

var imageEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
foreach (var entry in source.Entries)
{
    if (!entry.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || entry.Length < minImageBytes)
        continue;

    var name = Path.GetFileName(entry.FullName);
    if (string.IsNullOrEmpty(name))
        continue;

    var id = Path.GetFileNameWithoutExtension(name);
    imageEntries.TryAdd(id, entry);
}

Console.WriteLine($"Indexed {imageEntries.Count:N0} JPEGs >= {minImageBytes} bytes.");

var csvEntry = source.GetEntry("data.csv")
    ?? throw new InvalidOperationException("data.csv not found in source archive.");

var candidates = new List<(string ImageFile, string Description, string DisplayName, string Category, ZipArchiveEntry ImageEntry)>();
using (var reader = new StreamReader(csvEntry.Open()))
{
    var header = reader.ReadLine() ?? throw new InvalidOperationException("Empty CSV.");
    var index = BuildHeaderIndex(ParseCsvLine(header));

    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;

        var fields = ParseCsvLine(line);
        var imageFile = Get(fields, index, "image");
        var description = Get(fields, index, "description");
        var displayName = Get(fields, index, "display name");
        var category = Get(fields, index, "category");

        if (string.IsNullOrWhiteSpace(imageFile)
            || string.IsNullOrWhiteSpace(displayName)
            || string.IsNullOrWhiteSpace(category)
            || description.Trim().Length < minDescriptionChars)
        {
            continue;
        }

        var id = Path.GetFileNameWithoutExtension(imageFile.Trim());
        if (!imageEntries.TryGetValue(id, out var imageEntry))
            continue;

        candidates.Add((imageFile.Trim(), description.Trim(), displayName.Trim(), category.Trim(), imageEntry));
    }
}

Console.WriteLine($"Candidates with good metadata + image: {candidates.Count:N0}");

if (candidates.Count < targetCount)
{
    Console.Error.WriteLine($"Need at least {targetCount} candidates, found {candidates.Count}.");
    return 1;
}

var rng = new Random(randomSeed);
var selected = candidates.OrderBy(_ => rng.Next()).Take(targetCount).ToList();

Directory.CreateDirectory(Path.GetDirectoryName(outputZip)!);
if (File.Exists(outputZip))
    File.Delete(outputZip);

var csvLines = new StringBuilder();
csvLines.AppendLine("image,description,display name,category");
foreach (var row in selected)
{
    csvLines.AppendLine(string.Join(',',
        Csv(Path.GetFileName(row.ImageFile)),
        Csv(row.Description),
        Csv(row.DisplayName),
        Csv(row.Category)));
}

using (var output = ZipFile.Open(outputZip, ZipArchiveMode.Create))
{
    var csvOut = output.CreateEntry("data.csv", CompressionLevel.SmallestSize);
    await using (var csvStream = csvOut.Open())
    await using (var writer = new StreamWriter(csvStream, new UTF8Encoding(false)))
    {
        await writer.WriteAsync(csvLines.ToString()).ConfigureAwait(false);
    }

    var written = 0;
    foreach (var row in selected)
    {
        var id = Path.GetFileNameWithoutExtension(row.ImageFile);
        var destName = $"images/{id}.jpg";
        var dest = output.CreateEntry(destName, CompressionLevel.SmallestSize);
        await using var src = row.ImageEntry.Open();
        await using var dst = dest.Open();
        await src.CopyToAsync(dst).ConfigureAwait(false);
        written++;
        if (written % 1000 == 0)
            Console.WriteLine($"Packed {written:N0}/{targetCount:N0} images…");
    }
}

var size = new FileInfo(outputZip).Length;
Console.WriteLine($"Wrote {selected.Count:N0} rows to {outputZip} ({size / (1024.0 * 1024.0):F1} MB).");
return size > 95 * 1024 * 1024 ? 2 : 0;

static Dictionary<string, int> BuildHeaderIndex(IReadOnlyList<string> headers)
{
    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < headers.Count; i++)
        map[headers[i].Trim()] = i;
    return map;
}

static string Get(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> index, string name)
{
    if (!index.TryGetValue(name, out var pos) || pos < 0 || pos >= fields.Count)
        return "";
    return fields[pos].Trim();
}

static List<string> ParseCsvLine(string line)
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

static string Csv(string value)
{
    if (value.IndexOfAny(['"', ',', '\n', '\r']) < 0)
        return value;
    return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
