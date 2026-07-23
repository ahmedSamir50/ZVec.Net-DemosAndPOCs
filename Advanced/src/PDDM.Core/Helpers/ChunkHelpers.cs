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

/// <summary>Join helpers without magic separators in call sites.</summary>
public static class StringJoinHelper
{
    public const string Semicolon = ";";

    public static string JoinSemicolon(IEnumerable<string> items)
        => string.Join(Semicolon, items);
}
