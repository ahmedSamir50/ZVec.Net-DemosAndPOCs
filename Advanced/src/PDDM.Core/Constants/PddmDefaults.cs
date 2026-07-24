namespace PDDM.Core.Constants;

using PDDM.Shared;
using PDDM.Shared.Constants;

/// <summary>Compile-time and default numeric/string knobs for PDDM Core.</summary>
public static class PddmDefaults
{
    /// <summary>Locked embedding dimension for the ZVec collection schema.</summary>
    public const int EmbeddingDimensions = 768;

    public const int HnswM = 32;
    public const int HnswEfConstruction = 256;

    public const int DefaultTopKAssignedCross = 5;
    public const int DefaultTopKRequirement = 20;
    public const int DefaultTopKDecision = 10;
    public const int DefaultClusterCount = 3;
    public const int DefaultStandaloneHits = 5;

    public const int ContextMaxDescriptionChars = 1200;
    public const int ContextMaxRelatedStories = 10;
    public const int ContextMaxCrossRefs = 3;
    public const int ContextMaxDecisionComments = 5;

    /// <summary>Timeout for ambiguous-intent LLM classification.</summary>
    public const int IntentClassifyTimeoutMs = 5000;

    /// <summary>Max tokens for intent JSON classification completion.</summary>
    public const int IntentClassifyMaxTokens = 64;

    public const string ChunkIdsFileName = "chunk-ids.json";
    public const string CollectionName = "spark_docs";
    public const string DefaultCollectionPath = "./data/spark-docs";

    public const string DefaultLmStudioBaseUrl = "http://localhost:1234/v1";
    public const string DefaultEmbeddingModel = SharedPddmDefaults.DefaultEmbeddingModel;
    public const string DefaultChatModel = SharedPddmDefaults.DefaultChatModel;

    public const string DefaultJiraBaseUrl = "https://issues.apache.org/jira/rest/api/2";
    public const string DefaultJiraProjectKey = "SPARK";

    /// <summary>Apache Jira Epic Link custom field id (verified for issues.apache.org).</summary>
    public const string JiraEpicLinkField = "customfield_12311120";

    /// <summary>Shared navigator system prompt (scenario structure appended via <see cref="BuildSystemPrompt"/>).</summary>
    public const string SystemPromptBase = """
        You are Project Docs Deep Mind (PDDM), a Project Docs Navigator.
        Your job is to guide the developer through documentation hierarchy (UP = Epic/business, SIDE = siblings, DOWN = details/decisions) — NOT to dump retrieved text.

        Rules (mandatory):
        1. Answer ONLY from CONTEXT. Suggest running Ingestion ONLY when CONTEXT is empty or explicitly says the key/docs were not found. If CONTEXT lists any Epics, issues, or comments, you MUST navigate from them — do not refuse with “run Ingestion”.
        2. Synthesize briefly (short bullets / short sections). Do NOT paste raw CONTEXT, full descriptions, or long quotes unless a one-line decision excerpt is essential.
        3. Every cited Jira key MUST be a markdown link using the Url from CONTEXT, e.g. [SPARK-57337](https://issues.apache.org/jira/browse/SPARK-57337). Never invent hosts like jira.example.com.
        4. End with a "Sources" list of markdown links for keys you cited.
        """;

    /// <summary>Backward-compatible full system prompt (all scenarios). Prefer <see cref="BuildSystemPrompt"/>.</summary>
    public static string SystemPrompt => BuildSystemPrompt(QueryIntent.GeneralQuestion);

    /// <summary>System prompt with a single scenario structure directive.</summary>
    public static string BuildSystemPrompt(QueryIntent intent)
    {
        var structure = intent switch
        {
            QueryIntent.AssignedIssue =>
                "Structure: Assigned ticket — Epic → Your issue → Siblings / risks → useful decisions.",
            QueryIntent.NewRequirement =>
                "Structure: New requirement — Related landscape (top Epics/issues) → suggestion where a new Story might belong.",
            QueryIntent.DecisionRationale =>
                "Structure: Decision — Rationale summary → source issue/comment with links.",
            _ =>
                "Structure: General — Related landscape of existing work → point to the most relevant Epics/issues."
        };

        return $"{SystemPromptBase.TrimEnd()}\n        5. {structure}";
    }

    /// <summary>Wraps assembled context + user question for the chat model.</summary>
    public static string BuildUserPrompt(string context, string question, QueryIntent intent) =>
        $"""
        SCENARIO: {intent}

        CONTEXT (use only this; do not invent):
        {context}

        QUESTION:
        {question}

        Respond as a navigator with markdown links.
        """;

    /// <summary>System prompt for ambiguous-intent LLM classification (JSON only).</summary>
    public const string IntentClassifySystemPrompt = """
        Classify the user question into exactly one QueryIntent for a Jira project-docs navigator.
        Reply with ONLY compact JSON (no markdown): {"intent":"<name>","issueKey":"<KEY-or-null>"}
        intent must be one of: AssignedIssue, NewRequirement, DecisionRationale, GeneralQuestion.
        - AssignedIssue: user references or was assigned a specific Jira issue key.
        - NewRequirement: user wants to add/implement a feature or requirement.
        - DecisionRationale: user asks why a past decision was made / rationale.
        - GeneralQuestion: anything else about the project docs.
        """;
}

/// <summary>Decision-detection keyword list (no magic strings in detectors).</summary>
public static class DecisionKeywords
{
    public static readonly string[] All =
    [
        "decided", "decision", "agreed", "agree", "approved", "approve",
        "because", "rationale", "reason", "we will", "let's go with",
        "let us go with", "chosen", "chose", "preferred", "preference",
        "after discussion", "conclusion", "resolved to", "determined"
    ];
}

/// <summary>Auto-generated comment patterns to skip during ingestion.</summary>
public static class AutoGeneratedCommentPatterns
{
    public static readonly string[] All =
    [
        "has created a pull request",
        "This message was automatically generated",
        "ASF GitHub Bot"
    ];
}

/// <summary>Intent classifier phrase lists.</summary>
public static class IntentPhrases
{
    public static readonly string[] Decision =
    [
        "why did they", "what was the decision", "why was",
        "rationale for", "reason for choosing", "how did they decide",
        "what's the reasoning", "why did we choose", "decision behind"
    ];

    public static readonly string[] Requirement =
    [
        "i need to", "i want to add", "we should add",
        "i have to implement", "add validation",
        "new feature", "requirement", "i'm working on", "how to implement"
    ];
}
