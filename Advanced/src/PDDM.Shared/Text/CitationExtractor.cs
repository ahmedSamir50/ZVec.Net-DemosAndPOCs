using System.Text.RegularExpressions;
using PDDM.Shared.Dtos;

namespace PDDM.Shared.Text;

/// <summary>Extracts citation URLs from assembled RAG context text.</summary>
public static partial class CitationExtractor
{
    /// <summary>Finds unique citations from <c>Url:</c> lines and browse URLs in context.</summary>
    public static IReadOnlyList<CitationDto> Extract(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return [];

        var byUrl = new Dictionary<string, CitationDto>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in UrlLineRegex().Matches(context))
        {
            var url = match.Groups[1].Value.Trim().TrimEnd(')', ']', '.', ',');
            Add(byUrl, url);
        }

        foreach (Match match in BareBrowseUrlRegex().Matches(context))
        {
            var url = match.Value.Trim().TrimEnd(')', ']', '.', ',');
            Add(byUrl, url);
        }

        return byUrl.Values.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Add(Dictionary<string, CitationDto> byUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !uriLooksHttp(url))
            return;

        var key = TryKeyFromBrowseUrl(url) ?? "Link";
        byUrl[url] = new CitationDto { Key = key, Url = url };
    }

    private static bool uriLooksHttp(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the Jira issue key from a browse URL path, if present.</summary>
    public static string? TryKeyFromBrowseUrl(string url)
    {
        var match = BrowseKeyRegex().Match(url);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"Url:\s*(https?://\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlLineRegex();

    [GeneratedRegex(@"https?://issues\.apache\.org/jira/browse/[A-Za-z]+-\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareBrowseUrlRegex();

    [GeneratedRegex(@"/browse/([A-Za-z]+-\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrowseKeyRegex();
}
