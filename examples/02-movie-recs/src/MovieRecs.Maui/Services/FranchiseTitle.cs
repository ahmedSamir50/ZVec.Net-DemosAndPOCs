using System.Text.RegularExpressions;

namespace MovieRecs.Maui.Services;

/// <summary>
/// Franchise / sequel title stems for demo rerank (Matrix → Reloaded, Die Hard → sequels, …).
/// </summary>
internal static partial class FranchiseTitle
{
    private static readonly Regex YearSuffix = YearSuffixRegex();
    private static readonly Regex NonAlpha = NonAlphaRegex();

    private static readonly HashSet<string> SequelTailWords = new(StringComparer.Ordinal)
    {
        "reloaded", "revolutions", "returns", "revenge", "resurrection",
        "forever", "begins", "awakens", "rise", "fallen", "legacy",
        "vengeance", "untitled", "next", "another"
    };

    /// <summary>
    /// Normalize MovieLens title to a franchise stem, e.g.
    /// "Matrix, The (1999)" → "matrix"; "Die Hard: With a Vengeance (1995)" → "die hard".
    /// </summary>
    public static string Stem(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var t = title.Trim();
        t = YearSuffix.Replace(t, "").Trim();

        // MovieLens "Title, The" → "The Title"
        if (t.EndsWith(", The", StringComparison.OrdinalIgnoreCase))
            t = "The " + t[..^5];
        else if (t.EndsWith(", A", StringComparison.OrdinalIgnoreCase))
            t = "A " + t[..^3];

        // Keep text before subtitle colon for "Die Hard: With a Vengeance"
        var colon = t.IndexOf(':');
        if (colon > 0)
            t = t[..colon];

        t = t.ToLowerInvariant();
        t = NonAlpha.Replace(t, " ");
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tok => tok is not ("the" or "a" or "an"))
            .ToList();

        while (tokens.Count > 1 && (IsSequelMarker(tokens[^1]) || SequelTailWords.Contains(tokens[^1])))
            tokens.RemoveAt(tokens.Count - 1);

        // "matrix reloaded" after marker strip → already "matrix"
        if (tokens.Count == 0)
            return "";

        // Prefer 1–2 content tokens; keep "die hard", "lord of rings"-style short phrases
        if (tokens.Count >= 3 && tokens[1] is "of" or "and")
            return string.Join(' ', tokens.Take(3));
        return string.Join(' ', tokens.Take(Math.Min(2, tokens.Count)));
    }

    public static bool SharesFranchise(string watchlistTitle, string candidateTitle)
    {
        var a = Stem(watchlistTitle);
        var b = Stem(candidateTitle);
        if (a.Length < 3 || b.Length < 3)
            return false;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    private static bool IsSequelMarker(string tok) =>
        tok is "2" or "3" or "4" or "5" or "6" or "ii" or "iii" or "iv" or "v"
            or "part" or "chapter";

    [GeneratedRegex(@"\s*\(\d{4}\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex YearSuffixRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaRegex();
}
