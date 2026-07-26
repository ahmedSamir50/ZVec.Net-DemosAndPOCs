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

    /// <summary>Max description chars in CONTEXT (keeps keys/summaries/Urls intact; cuts echo-dumps).</summary>
    public const int ContextMaxDescriptionChars = 500;

    public const int ContextMaxRelatedStories = 8;
    public const int ContextMaxCrossRefs = 3;
    public const int ContextMaxDecisionComments = 5;
    public const int ContextMaxSubTasks = 8;

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
        1. First decide: if CONTEXT lists any Epics, issues, comments, or Url lines, you MUST navigate from them. Suggest running Ingestion ONLY when CONTEXT is empty or explicitly says the key/docs were not found.
        2. Synthesize briefly: at most 8–12 short bullets total; each section ≤ 2–3 lines. Do NOT paste raw CONTEXT, full descriptions, or repeat headers like "EPIC:" / "Url:" blocks verbatim. Prefer one-line decision excerpts only when essential.
        3. Every cited Jira key MUST be a markdown link using the Url from CONTEXT, e.g. [SPARK-57337](https://issues.apache.org/jira/browse/SPARK-57337). Never invent hosts like jira.example.com. Never invent SPARK keys not present in CONTEXT.
        4. End with a "Sources" list of markdown links for keys you cited.
        """;

    /// <summary>Backward-compatible full system prompt (all scenarios). Prefer <see cref="BuildSystemPrompt"/>.</summary>
    public static string SystemPrompt => BuildSystemPrompt(QueryIntent.GeneralQuestion);

    /// <summary>System prompt with a single scenario structure directive and output skeleton.</summary>
    public static string BuildSystemPrompt(QueryIntent intent)
    {
        var (structure, skeleton) = intent switch
        {
            QueryIntent.AssignedIssue => (
                "Structure: Assigned ticket — Epic → Your issue → Siblings / risks → useful decisions / sub-tasks.",
                """
                Output skeleton:
                ### Epic
                - [KEY](url) — one-line business purpose
                ### Your issue
                - [KEY](url) — what it is / why it matters
                ### Siblings / risks
                - open siblings or risks (or "none noted")
                ### Sources
                - markdown links
                """),
            QueryIntent.NewRequirement => (
                "Structure: New requirement — Related landscape (top Epics/issues) → suggestion where a new Story might belong.",
                """
                Output skeleton:
                ### Related landscape
                - top 1–3 Epics/issues with [KEY](url) and one-line why relevant
                ### Where to attach
                - suggested Epic (or standalone) for a new Story
                ### Sources
                - markdown links
                """),
            QueryIntent.DecisionRationale => (
                "Structure: Decision — Rationale summary → source issue/comment with links.",
                """
                Output skeleton:
                ### Rationale
                - one short summary sentence; if a decision comment exists, include one short quoted one-liner
                ### Sources
                - parent issue + comment links from CONTEXT only
                """),
            _ => (
                "Structure: General — Related landscape of existing work → point to the most relevant Epics/issues.",
                """
                Output skeleton:
                ### Related landscape
                - most relevant Epics/issues with [KEY](url)
                ### Sources
                - markdown links
                """)
        };

        return $"{SystemPromptBase.TrimEnd()}\n        5. {structure}\n\n{skeleton.Trim()}";
    }

    /// <summary>Wraps assembled context + user question for the chat model.</summary>
    public static string BuildUserPrompt(string context, string question, QueryIntent intent) =>
        $"""
        SCENARIO: {intent}

        CONTEXT (use only this; do not invent):
        {context}

        QUESTION:
        {question}

        Follow the system Structure and output skeleton exactly. Put Sources last. Use markdown links only from CONTEXT Url lines.
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

        Examples:
        Q: I got assigned SPARK-57337 — help me understand it
        A: {"intent":"AssignedIssue","issueKey":"SPARK-57337"}
        Q: I need to add ANSI mode validation so invalid string-to-number casts throw
        A: {"intent":"NewRequirement","issueKey":null}
        Q: Why did they decide to enable ANSI mode by default in Spark 4.0?
        A: {"intent":"DecisionRationale","issueKey":null}
        Q: Tell me about streaming shuffle coordination
        A: {"intent":"GeneralQuestion","issueKey":null}
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
        "what's the reasoning", "why did we choose", "decision behind",
        "enable by default", "why enable", "why did they decide to enable",
        "explain the ansi", "ansi mode choice", "reasoning behind"
    ];

    public static readonly string[] Requirement =
    [
        "i need to", "i want to add", "we should add",
        "i have to implement", "add validation",
        "new feature", "requirement", "i'm working on", "how to implement",
        "string-to-number", "add ansi", "help me implement",
        "i need to add", "invalid string-to-number"
    ];
}
