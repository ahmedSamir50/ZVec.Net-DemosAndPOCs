using PDDM.Core.Constants;
using PDDM.Shared.Constants;

namespace PDDM.Core.Helpers;

/// <summary>Formats ZVec document ids from Jira issue types.</summary>
public static class ChunkIdFormatter
{
    /// <summary>Builds issue chunk id for a Jira issue type + key.</summary>
    public static string FormatIssueId(string issueType, string key) => issueType switch
    {
        JiraIssueTypeNames.Epic => ChunkIdPrefixes.Epic + key,
        JiraIssueTypeNames.Umbrella => ChunkIdPrefixes.Umbrella + key,
        JiraIssueTypeNames.Story => ChunkIdPrefixes.Story + key,
        JiraIssueTypeNames.Bug => ChunkIdPrefixes.Bug + key,
        JiraIssueTypeNames.Improvement => ChunkIdPrefixes.Improvement + key,
        JiraIssueTypeNames.Task => ChunkIdPrefixes.Task + key,
        JiraIssueTypeNames.NewFeature => ChunkIdPrefixes.Feature + key,
        JiraIssueTypeNames.SubTask => ChunkIdPrefixes.Subtask + key,
        _ => ChunkIdPrefixes.Issue + key
    };

    /// <summary>Builds comment chunk id.</summary>
    public static string FormatCommentId(string issueKey, int index)
        => $"{ChunkIdPrefixes.Comment}{issueKey}_{index}";

    /// <summary>
    /// Part 0 keeps <paramref name="canonicalId"/>; parts 1..N append <c>_pN</c>.
    /// </summary>
    public static string FormatPartId(string canonicalId, int partIndex)
        => partIndex <= 0 ? canonicalId : $"{canonicalId}_p{partIndex}";

    /// <summary>True when id is not an embedding split part (<c>_pN</c> suffix).</summary>
    public static bool IsCanonicalChunkId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return true;

        var idx = id.LastIndexOf("_p", StringComparison.Ordinal);
        if (idx < 0 || idx + 2 >= id.Length)
            return true;

        for (var i = idx + 2; i < id.Length; i++)
        {
            if (!char.IsAsciiDigit(id[i]))
                return true;
        }

        return false;
    }

    /// <summary>Candidate ids when resolving a bare Jira key.</summary>
    public static IReadOnlyList<string> PossibleIdsForKey(string key) =>
    [
        key,
        ChunkIdPrefixes.Epic + key,
        ChunkIdPrefixes.Umbrella + key,
        ChunkIdPrefixes.Story + key,
        ChunkIdPrefixes.Bug + key,
        ChunkIdPrefixes.Improvement + key,
        ChunkIdPrefixes.Task + key,
        ChunkIdPrefixes.Feature + key,
        ChunkIdPrefixes.Subtask + key,
        ChunkIdPrefixes.Issue + key
    ];
}

/// <summary>Maps issue type name to <see cref="PDDM.Shared.DocTier"/> int value.</summary>
public static class TierMapper
{
    public static int DetermineTier(string issueType) => issueType switch
    {
        JiraIssueTypeNames.Epic => (int)PDDM.Shared.DocTier.EpicOrUmbrella,
        JiraIssueTypeNames.Umbrella => (int)PDDM.Shared.DocTier.EpicOrUmbrella,
        JiraIssueTypeNames.Story => (int)PDDM.Shared.DocTier.Issue,
        JiraIssueTypeNames.Bug => (int)PDDM.Shared.DocTier.Issue,
        JiraIssueTypeNames.Improvement => (int)PDDM.Shared.DocTier.Issue,
        JiraIssueTypeNames.Task => (int)PDDM.Shared.DocTier.Issue,
        JiraIssueTypeNames.NewFeature => (int)PDDM.Shared.DocTier.Issue,
        JiraIssueTypeNames.SubTask => (int)PDDM.Shared.DocTier.SubTask,
        _ => (int)PDDM.Shared.DocTier.Issue
    };
}

/// <summary>Text truncation helpers for context assembly.</summary>
public static class TextTruncator
{
    public static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text ?? "";
        return text[..maxChars] + "...";
    }
}

/// <summary>
/// Splits long bodies so each piece fits an embedding char budget.
/// Break preference: paragraph → newline → sentence → word → hard cut.
/// </summary>
public static class EmbeddingTextSplitter
{
    /// <summary>Splits <paramref name="text"/> into segments each ≤ <paramref name="maxPartChars"/>.</summary>
    public static IReadOnlyList<string> Split(string text, int maxPartChars)
    {
        if (maxPartChars < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPartChars));

        if (string.IsNullOrEmpty(text))
            return [""];

        if (text.Length <= maxPartChars)
            return [text];

        var parts = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= maxPartChars)
            {
                parts.Add(text[offset..].Trim());
                break;
            }

            var cut = FindBreak(text.AsSpan(offset, maxPartChars));
            var slice = text.Substring(offset, cut).Trim();
            if (slice.Length == 0)
            {
                // Avoid infinite loop on whitespace-only windows.
                cut = maxPartChars;
                slice = text.Substring(offset, cut);
            }

            parts.Add(slice);
            offset += cut;
            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
                offset++;
        }

        return parts.Count > 0 ? parts : [text[..Math.Min(maxPartChars, text.Length)]];
    }

    private static int FindBreak(ReadOnlySpan<char> window)
    {
        var minKeep = Math.Max(1, window.Length / 4);

        var para = LastIndexOf(window, "\n\n");
        if (para >= minKeep)
            return para + 2;

        var line = LastIndexOf(window, "\n");
        if (line >= minKeep)
            return line + 1;

        var sentence = LastIndexOf(window, ". ");
        if (sentence >= minKeep)
            return sentence + 2;

        var space = window.LastIndexOf(' ');
        if (space >= minKeep)
            return space + 1;

        return window.Length;
    }

    private static int LastIndexOf(ReadOnlySpan<char> haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle.AsSpan()))
                return i;
        }

        return -1;
    }
}

/// <summary>Join helpers without magic separators in call sites.</summary>
public static class StringJoinHelper
{
    public const string Semicolon = ";";

    public static string JoinSemicolon(IEnumerable<string> items)
        => string.Join(Semicolon, items);
}
