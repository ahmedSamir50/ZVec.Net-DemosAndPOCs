namespace PDDM.Shared.Constants;

/// <summary>Named <see cref="System.Net.Http.HttpClient"/> registrations.</summary>
public static class HttpClientNames
{
    public const string LmStudio = "LmStudio";
    public const string Jira = "Jira";
    public const string PddmApi = "PddmApi";
}

/// <summary>Configuration section names bound via Options.</summary>
public static class ConfigurationSections
{
    public const string Pddm = "Pddm";
    public const string ZVec = "ZVec";
    public const string PddmUi = "PddmUi";
}

/// <summary>API route path segments (no leading slash on group; full paths include slash).</summary>
public static class ApiRoutes
{
    public const string ChatStream = "/api/chat/stream";
    public const string Ingestion = "/api/ingestion";
    public const string Stats = "/api/stats";
    public const string Settings = "/api/settings";
    public const string SettingsVerify = "/api/settings/verify";
}

/// <summary>SSE event type names sent from API to UI.</summary>
public static class SseEventTypes
{
    public const string Intent = "intent";
    public const string Token = "token";
    public const string Done = "done";
    public const string Error = "error";
    public const string Progress = "progress";
}

/// <summary>Chunk document Id prefixes stored in ZVec.NET.</summary>
public static class ChunkIdPrefixes
{
    public const string Epic = "epic_";
    public const string Umbrella = "umbrella_";
    public const string Story = "story_";
    public const string Bug = "bug_";
    public const string Improvement = "improvement_";
    public const string Task = "task_";
    public const string Feature = "feature_";
    public const string Subtask = "subtask_";
    public const string Issue = "issue_";
    public const string Comment = "comment_";
}

/// <summary>Jira issue type display names from ASF Jira.</summary>
public static class JiraIssueTypeNames
{
    public const string Epic = "Epic";
    public const string Umbrella = "Umbrella";
    public const string Story = "Story";
    public const string Bug = "Bug";
    public const string Improvement = "Improvement";
    public const string Task = "Task";
    public const string NewFeature = "New Feature";
    public const string SubTask = "Sub-task";
    public const string Comment = "Comment";
}

/// <summary>CORS policy name.</summary>
public static class CorsPolicyNames
{
    public const string AllowUi = "AllowUI";
}

/// <summary>Sidecar file names next to the ZVec collection.</summary>
public static class StorageFileNames
{
    public const string ChunkIdsJson = "chunk-ids.json";
}

/// <summary>Shared numeric defaults safe for UI and API.</summary>
public static class SharedPddmDefaults
{
    /// <summary>Locked embedding dimension for the ZVec collection schema.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>Default chat model id (LM Studio) — shipped fallback.</summary>
    public const string DefaultChatModel = "google/gemma-4-e2b";

    /// <summary>
    /// Recommended chat model for demos (LM Studio). Use Q4_K_M; on 4 GB VRAM enable GPU+CPU/RAM offload.
    /// </summary>
    public const string RecommendedChatModel = "lmstudio-community/Qwen2.5-7B-Instruct-GGUF";

    /// <summary>Default embedding model id (LM Studio).</summary>
    public const string DefaultEmbeddingModel = "text-embedding-nomic-embed-text-v1.5";

    /// <summary>Public Jira browse URL template; {0} = issue key.</summary>
    public const string JiraBrowseUrlFormat = "https://issues.apache.org/jira/browse/{0}";

    /// <summary>Jira search/issue fields required for PDDM (comment is a field, not an expand).</summary>
    public const string JiraIssueFields =
        "summary,description,issuetype,status,priority,assignee,components,labels,fixVersions,parent,subtasks,issuelinks,comment,customfield_12311120";

    /// <summary>User-Agent for ASF Jira anonymous clients.</summary>
    public const string JiraUserAgent = "PDDM/1.0 (ZVec.Net demo; +https://github.com/ahmedSamir50/ZVec.Net-DemosAndPOCs)";
}

/// <summary>Issue keys that must be present for golden demo Q1–Q3 after ingest.</summary>
public static class GoldenDemoSeedKeys
{
    public const string AssignedTicket = "SPARK-57337";
    public const string AnsiDefaultDecision = "SPARK-44444";

    /// <summary>All seed keys in priority order.</summary>
    public static readonly string[] All =
    [
        AssignedTicket,
        AnsiDefaultDecision
    ];
}

/// <summary>SSE progress phase names for chat pipeline.</summary>
public static class ChatProgressPhases
{
    public const string Classifying = "classifying";
    public const string Retrieving = "retrieving";
    public const string Generating = "generating";
}

/// <summary>Golden demo prompts (UI chips + DEMO.md — keep in sync).</summary>
public static class GoldenDemoQuestions
{
    public const string AssignedTicket =
        "I got assigned SPARK-57337 — help me understand it";

    public const string NewRequirement =
        "I need to add ANSI mode validation so invalid string-to-number casts throw instead of returning null";

    public const string DecisionRationale =
        "Why did they decide to enable ANSI mode by default in Spark 4.0?";

    /// <summary>All golden prompts in display order.</summary>
    public static readonly string[] All =
    [
        AssignedTicket,
        NewRequirement,
        DecisionRationale
    ];
}
