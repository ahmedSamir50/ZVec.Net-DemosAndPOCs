using PDDM.Shared;

namespace PDDM.Core.Models;

/// <summary>Assembled hierarchy context for RAG prompt building.</summary>
public sealed class NavigatedContext
{
    public JiraDocChunk? CentralIssue { get; set; }
    public JiraDocChunk? ParentEpic { get; set; }
    public List<JiraDocChunk> SiblingIssues { get; set; } = [];
    public List<JiraDocChunk> SubTasks { get; set; } = [];
    public List<JiraDocChunk> DecisionComments { get; set; } = [];
    public List<JiraDocChunk> CrossReferences { get; set; } = [];

    public string? RequirementText { get; set; }
    public List<JiraDocChunk> RelatedEpics { get; set; } = [];
    public List<JiraDocChunk> RelatedStories { get; set; } = [];
    public List<JiraDocChunk> StandaloneRelatedIssues { get; set; } = [];

    public List<JiraDocChunk> ParentIssues { get; set; } = [];
    public List<JiraDocChunk> ParentEpics { get; set; } = [];

    public QueryIntent Intent { get; set; }
    public string AssembledContext { get; set; } = "";

    /// <summary>Creates a not-found context for an issue key.</summary>
    public static NavigatedContext NotFound(string key)
    {
        var url = string.Format(PDDM.Shared.Constants.SharedPddmDefaults.JiraBrowseUrlFormat, key);
        return new()
        {
            Intent = QueryIntent.AssignedIssue,
            AssembledContext =
                $"No documentation found for issue key: {key}. Url: {url}. Run Ingest (or seed) so this key is indexed."
        };
    }
}

/// <summary>Ingestion pipeline progress.</summary>
public sealed class IngestionProgress
{
    public int IssuesFetched { get; set; }
    public int ChunksCreated { get; set; }
    public int EmbeddingsGenerated { get; set; }
    public int ChunksInserted { get; set; }
    public string Status { get; set; } = IngestionStatus.NotStarted;
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Ingestion status values.</summary>
public static class IngestionStatus
{
    public const string NotStarted = "NotStarted";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}
