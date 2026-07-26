using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PDDM.Shared.Text;

/// <summary>Formats chat answers as safe HTML with short hyperlinks.</summary>
public static partial class ChatAnswerHtml
{
    /// <summary>
    /// HTML-encodes <paramref name="answer"/>, then converts markdown links and bare URLs
    /// into short <c>&lt;a&gt;</c> tags (label, Jira key, or "Link").
    /// </summary>
    public static string Format(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
            return "";

        var sb = new StringBuilder(answer.Length + 64);
        var span = answer.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            if (TryMatchMarkdown(span, i, out var mdLen, out var label, out var mdUrl))
            {
                AppendAnchor(sb, mdUrl, string.IsNullOrWhiteSpace(label) ? DisplayTextForUrl(mdUrl) : label);
                i += mdLen;
                continue;
            }

            if (TryMatchBareUrl(span, i, out var urlLen, out var bareUrl))
            {
                AppendAnchor(sb, bareUrl, DisplayTextForUrl(bareUrl));
                i += urlLen;
                continue;
            }

            // Encode one character (handle surrogate pairs simply via string slice of 1 rune when needed)
            var next = i + 1;
            while (next < span.Length
                   && !CouldStartLink(span, next)
                   && span[next] != '[')
            {
                next++;
            }

            sb.Append(WebUtility.HtmlEncode(span[i..next].ToString()));
            i = next;
        }

        return sb.ToString();
    }

    /// <summary>Display text for a bare URL: Jira key or "Link".</summary>
    public static string DisplayTextForUrl(string url)
        => CitationExtractor.TryKeyFromBrowseUrl(url) ?? "Link";

    private static bool CouldStartLink(ReadOnlySpan<char> span, int index)
        => index < span.Length
           && (span[index] == '['
               || (span[index] == 'h'
                   && index + 7 < span.Length
                   && (span[index..].StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || span[index..].StartsWith("https://", StringComparison.OrdinalIgnoreCase))));

    private static bool TryMatchMarkdown(
        ReadOnlySpan<char> span,
        int index,
        out int length,
        out string label,
        out string url)
    {
        length = 0;
        label = "";
        url = "";
        if (span[index] != '[')
            return false;

        var slice = span[index..].ToString();
        var match = MarkdownLinkRegex().Match(slice);
        if (!match.Success || match.Index != 0)
            return false;

        label = match.Groups[1].Value;
        url = match.Groups[2].Value;
        if (!IsHttpUrl(url))
            return false;

        length = match.Length;
        return true;
    }

    private static bool TryMatchBareUrl(
        ReadOnlySpan<char> span,
        int index,
        out int length,
        out string url)
    {
        length = 0;
        url = "";
        var slice = span[index..];
        if (!slice.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !slice.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = BareUrlRegex().Match(slice.ToString());
        if (!match.Success || match.Index != 0)
            return false;

        url = match.Value.TrimEnd(')', ']', '.', ',', ';');
        if (!IsHttpUrl(url))
            return false;

        length = url.Length;
        return true;
    }

    private static void AppendAnchor(StringBuilder sb, string url, string label)
    {
        sb.Append("<a href=\"")
            .Append(WebUtility.HtmlEncode(url))
            .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
            .Append(WebUtility.HtmlEncode(label))
            .Append("</a>");
    }

    private static bool IsHttpUrl(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\[([^\]]*)\]\((https?://[^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"^https?://[^\s<>""']+", RegexOptions.CultureInvariant)]
    private static partial Regex BareUrlRegex();
}
