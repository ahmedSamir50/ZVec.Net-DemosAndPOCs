# ZVecRAG POC Plan — "Project Docs Smart Mind"

> **Product Vision**: A smart navigator that chats with your project docs (Epics, Stories, Bugs, Comments, Verifications) to reduce the 15–45 minutes a developer spends manually searching for context — down to 30 seconds.

---

## 1. Problem Statement

### The Pain

When a developer receives work — whether it's an assigned ticket, a bug report, or a brand-new requirement from a stakeholder — they must manually search across dozens of issues, epics, comments, and linked artifacts to build full business context. This takes 15–45 minutes, touches many unrelated places, and often misses critical connections (decisions, related bugs, sibling stories, verification criteria).

The problem exists in **three distinct scenarios**, not just one:

### Scenario A — Assigned Ticket

> "I got assigned SPARK-57337, help me understand it"

The developer has a known issue key. They need to navigate UP (what Epic/business goal), SIDE (what sibling stories exist), and DOWN (what decisions/comments were made). The hierarchy already exists in the project docs — they just can't find it quickly.

### Scenario B — New Requirement (No Existing Ticket)

> "I need to add validation to tenant onboarding: when X != Y, the user should update their profile CR number first"

There is NO epic, story, bug, or ticket for this yet. But the developer knows what they need. The system must find **relevant existing context** — epics about "tenant onboarding", stories about "validation", bugs about "profile updates", comments about "CR number handling" — and assemble a navigable landscape of related work so the developer understands what already exists before writing new code or creating a ticket.

### Scenario C — Decision / Rationale Question

> "Why did they decide to use Netty for the shuffle protocol?"

The developer needs to understand a past decision. The answer lives in comments and discussions, not in issue descriptions. The system must find the relevant comments (decision-flagged) and navigate UP to provide the issue/epic context surrounding that decision.

---

## 2. Data Source

### Primary: Apache Spark on Apache Jira

**URL**: `https://issues.apache.org/jira/projects/SPARK`

**Why this data source**: It's a REAL Atlassian Jira instance, fully public, no authentication required, with a working REST API that returns complete hierarchy, comments, and metadata.

| Artifact Type | Count | Verified |
|---|---|---|
| Total Issues | 57,850 | ✅ |
| Epics | 74 | ✅ |
| Stories | 138 | ✅ |
| Umbrella (initiatives) | 506 | ✅ |
| Bugs | 18,739 | ✅ |
| Improvements | 16,233 | ✅ |
| Tasks | 1,257 | ✅ |
| Sub-tasks | 15,305 | ✅ |
| Test | 1,146 | ✅ |
| Comments | Accessible via `expand=comments` | ✅ |
| Components | SQL, PySpark, MLlib, Structured Streaming, Spark Core, etc. | ✅ |
| Versions | 4.0.0, 4.1.0, 4.2.0, 4.3.0 | ✅ |
| Labels | Yes | ✅ |
| Priority/Status/Resolution | Full workflow | ✅ |
| Parent-child links | Umbrella → Sub-tasks/Links, Epic → Stories (via Epic Link field) | ✅ |

**Key API Endpoints (no auth needed)**:

```bash
# Epics
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+issuetype=Epic&maxResults=100&expand=comments'

# Stories
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+issuetype=Story&maxResults=200&expand=comments'

# Umbrella (initiatives)
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+issuetype=Umbrella&maxResults=500&expand=comments'

# Bugs (rich with comments)
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+issuetype=Bug&maxResults=500&expand=comments'

# Improvements (rich descriptions)
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+issuetype=Improvement&maxResults=500&expand=comments'

# Single issue with full details + comments
curl 'https://issues.apache.org/jira/rest/api/2/issue/SPARK-{ID}?expand=comments'

# Children of an Epic (via Epic Link custom field)
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+"Epic+Link"=SPARK-{EPIC_ID}&maxResults=50'

# All issues in a specific component
curl 'https://issues.apache.org/jira/rest/api/2/search?jql=project=SPARK+AND+component=SQL&maxResults=100'
```

**Hierarchy verified live**:

```
Umbrella (506) ──► Sub-tasks + Related Links (via issuelinks field)
    │
Epic (74) ──► Stories (138) + Issues (via Epic Link field: customfield_12311120)
    │
Issue (Bug/Improvement/Task) ──► Sub-tasks (via subtasks field) + Comments
    │
Comment ──► Decision discussions, rationale, verification notes
```

**API Caveats**:
- `ORDER BY comments` is NOT supported — must fetch and sort locally by comment count
- Many Epics have 0 comments — rich discussions live on Bugs and Improvements
- `expand=comments` returns comments inline — no separate endpoint needed
- Rate limiting is generous for unauthenticated access

### Why NOT GitLab.org

GitLab.org (gitlab.com/gitlab-org) has 22,000+ epics and rich metadata, BUT:
- **Issue comments/notes return 401 Unauthorized** — locked behind authentication
- **Epic-issues endpoint is Cloudflare/geoblocked** from certain regions
- Without comments, you only get half the picture — issue titles and descriptions without the decision rationale

This makes GitLab.org unusable for a POC that needs full comment/discussion access. It could be used in production with proper API tokens, but not for a public no-auth POC.

---

## 3. Architecture

### 3.1 High-Level Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                        USER INPUT                               │
│                                                                 │
│  Scenario A: "I got assigned SPARK-57337"                       │
│  Scenario B: "I need to add validation to tenant onboarding"    │
│  Scenario C: "Why did they use Netty for shuffle?"              │
└──────────────────────────┬──────────────────────────────────────┘
                           │
              ┌────────────▼────────────┐
              │   Intent Classifier      │
              │   (hybrid)               │
              │  Heuristic fast path;    │
              │  LLM JSON when ambiguous │
              │  A = assigned_issue      │
              │  B = new_requirement     │
              │  C = decision_rationale  │
              └────────────┬────────────┘
                           │
         ┌─────────────────▼──────────────────┐
         │         Retrieval Engine            │
         │                                    │
         │  Scenario A: Direct fetch by key   │
         │    → ZVec.NET Fetch(ID)            │
         │    → Then navigate UP/SIDE/DOWN    │
         │                                    │
         │  Scenario B: HNSW vector search    │
         │    → Embed requirement text        │
         │    → ZVec.NET HNSW query (Cosine)  │
         │    → Find relevant epics, stories, │
         │      bugs, improvements            │
         │    → Then navigate from each hit   │
         │                                    │
         │  Scenario C: HNSW + filter         │
         │    → ContainsDecision=true         │
         │    → Find decision comments        │
         │    → Then navigate UP to context   │
         └─────────────────┬──────────────────┘
                           │
         ┌─────────────────▼──────────────────┐
         │       Navigation Engine             │
         │                                     │
         │  From each retrieval result:        │
         │                                     │
         │  NAVIGATE UP:                       │
         │    Issue → parent Epic → Umbrella   │
         │                                     │
         │  NAVIGATE SIDE:                     │
         │    Epic → sibling stories/issues    │
         │    Component → related issues       │
         │                                     │
         │  NAVIGATE DOWN:                     │
         │    Issue → sub-tasks                │
         │    Issue → decision comments        │
         │                                     │
         │  All navigation uses metadata       │
         │  stored in ZVec.NET (EpicLink,      │
         │  ParentKey, Components, etc.)       │
         │  NOT additional vector queries.     │
         └─────────────────┬──────────────────┘
                           │
         ┌─────────────────▼──────────────────┐
         │       Context Builder               │
         │                                     │
         │  Assembles context in HIERARCHICAL   │
         │  ORDER for the LLM:                 │
         │                                     │
         │  📌 [Business Header] — Epic/Umbrella│
         │  📋 [Work Items] — Stories, Bugs     │
         │  🔗 [Cross-references] — Related     │
         │  💬 [Decisions] — Comments (when     │
         │      relevant to the question)       │
         │                                     │
         │  DECIDES what depth to include:      │
         │  - Overview question → Epic +        │
         │    story summaries only              │
         │  - New requirement → Related epics + │
         │    stories + relevant bugs +         │
         │    patterns from comments            │
         │  - Decision question → Epic header + │
         │    specific comment + surrounding     │
         │    issue context                     │
         └─────────────────┬──────────────────┘
                           │
         ┌─────────────────▼──────────────────┐
         │       LM Studio (Local LLM)         │
         │                                     │
         │  POST /v1/chat/completions           │
         │                                     │
         │  System prompt:                      │
         │  "You are a Project Docs Navigator.  │
         │   Guide the developer through the    │
         │   project documentation hierarchy.   │
         │   Show them what's ABOVE (business   │
         │   context), BESIDE (scope/siblings), │
         │   and BELOW (details/decisions)      │
         │   their current position in the      │
         │   project doc tree.                  │
         │   For new requirements, find the     │
         │   most relevant existing work and     │
         │   help the developer understand what │
         │   landscape they're working in."     │
         │                                     │
         │  User prompt: assembled context +    │
         │  original question                   │
         └─────────────────────────────────────┘
```

### 3.2 Scenario Flows (Detailed)

#### Scenario A — Assigned Ticket

```
User: "I got assigned SPARK-57337"

Step 1: Direct Fetch
  → ZVec.NET Fetch("SPARK-57337")
  → Returns: issue chunk with EpicLink="SPARK-56664", IssueType="Story"

Step 2: Navigate UP — Parent Epic
  → ZVec.NET Fetch("SPARK-56664")
  → Returns: Epic "Streaming Shuffle Coordination"
  → THIS IS THE BUSINESS HEADER

Step 3: Navigate SIDE — Sibling Stories
  → ZVec.NET Query with filter: EpicLink == "SPARK-56664" AND Tier == 1
  → Returns: 7 sibling stories (Part 1 through Part 7)

Step 4: Navigate UP-UP — Umbrella (if exists)
  → Check Epic's UmbrellaKey field
  → If found, Fetch the Umbrella for higher-level business context

Step 5: Navigate DOWN — Sub-tasks & Comments
  → ZVec.NET Query: ParentKey == "SPARK-57337" AND Tier >= 2
  → Returns: sub-tasks and decision comments

Step 6: Navigate CROSS — Related Bugs/Improvements in same Component
  → ZVec.NET Query: Components == "Spark Core" AND Tier == 1
  → Returns: cross-referenced issues in the same area

Step 7: Context Assembly
  → Umbrella header (if exists)
  → Epic header (always)
  → Sibling stories (always for assigned issue)
  → The assigned issue detail
  → Sub-tasks and comments (if any)
  → Related cross-references (if any)
```

#### Scenario B — New Requirement (No Existing Ticket)

```
User: "I need to add validation to tenant onboarding: when X != Y, 
       the user should update their profile CR number first"

Step 1: Embed the Requirement
  → LM Studio POST /v1/embeddings
  → Returns: float[768] vector for the requirement text

Step 2: Multi-Tier HNSW Search
  → ZVec.NET HNSW Query (Cosine, topK=15)
  → NO tier filter — search across ALL tiers
  → The requirement might match:
    - An Epic about "tenant onboarding"
    - A Story about "validation rules"
    - A Bug about "profile update failures"
    - A Comment discussing "CR number handling"

Step 3: Cluster Results by Epic
  → Group the top-K results by their EpicLink field
  → Each cluster represents a "related feature area"
  → Pick the top 2-3 clusters (most hits)

Step 4: Navigate from Each Cluster Hit
  → For the top cluster (e.g., Epic about tenant onboarding):
    - Fetch the Epic header → business context
    - Fetch sibling stories → what work already exists
    - Fetch related bugs → what problems exist
    - Fetch decision comments → what was decided

Step 5: Context Assembly (NEW REQUIREMENT STYLE)
  → "No exact match found for your requirement."
  → "But here's the relevant landscape:"
  → Epic headers for related feature areas
  → Stories that are closest to your requirement
  → Bugs that might be related
  → Decision comments that inform your approach
  → Suggestion: "Based on existing patterns in these epics, 
     you might want to create a story under Epic X that 
     covers validation logic similar to Story Y."
```

#### Scenario C — Decision / Rationale

```
User: "Why did they decide to use Netty for shuffle?"

Step 1: Embed the Question
  → LM Studio POST /v1/embeddings → float[768]

Step 2: Targeted HNSW Search
  → ZVec.NET HNSW Query (Cosine, topK=10)
  → Filter: ContainsDecision == true AND Tier == 3
  → Searches ONLY in decision-flagged comments

Step 3: Navigate UP from Decision Comments
  → For each hit comment:
    - Fetch parent issue (ParentKey) → "what was the discussion about?"
    - Fetch parent Epic (EpicLink) → "what feature area?"

Step 4: Context Assembly
  → Epic header (business context for the decision)
  → Parent issue (what triggered the discussion)
  → Decision comment(s) (the actual rationale)
  → Any linked sub-tasks that implemented the decision
```

### 3.3 When to Provide What Context

This is the **core intelligence** of the system — not dumping everything, but choosing the right depth based on scenario and question type:

| Scenario | Epic Header | Stories/Tasks | Bugs/Improvements | Comments | Sub-tasks |
|---|---|---|---|---|---|
| A: Assigned issue | ✅ Always | ✅ Always (siblings) | ✅ If related by component | ✅ Only decision ones | ✅ If exist |
| B: New requirement | ✅ Top 2-3 related | ✅ Closest matches | ✅ If related to the problem | ✅ If they show patterns | ❌ Usually skip |
| C: Decision rationale | ✅ Always (for context) | ❌ Usually skip | ✅ If the decision was on a bug | ✅ The core content | ❌ Skip |
| B→ narrowing follow-up | ✅ Focus on one | ✅ Deep dive on one epic | ✅ All related | ✅ All relevant | ✅ If user asks deeper |

**Key principle**: Comments are included **only when they contain decisions, rationale, or verification notes relevant to the question** — NOT every comment on every related issue.

---

## 4. Chunking Strategy

### 4.1 Four-Tier Chunk Model

Each Jira artifact is decomposed into tiered chunks with rich metadata for navigation:

```
Tier 0 — Umbrella / Epic
  ├── One chunk per Epic/Umbrella
  ├── Content: summary + description + objectives
  ├── Metadata: key, epicLink (for Umbrellas pointing to child Epics), 
  │             components, fixVersions, status, priority
  └── Purpose: BUSINESS HEADER — always first in context when children are retrieved

Tier 1 — Issue (Story, Bug, Improvement, Task, New Feature)
  ├── One chunk per issue
  ├── Content: summary + description + acceptance criteria
  ├── Metadata: key, issueType, epicLink (parent Epic key), 
  │             parentKey (for sub-tasks' parent), components,
  │             fixVersions, labels, status, priority, assignee
  └── Purpose: WORK ITEM — medium grain detail

Tier 2 — Sub-task
  ├── One chunk per sub-task
  ├── Content: summary + description
  ├── Metadata: key, parentKey (parent issue key), epicLink (inherited from parent)
  └── Purpose: Granular work detail — included only when user asks deeper

Tier 3 — Comment / Discussion
  ├── One chunk per comment
  ├── Content: comment body
  ├── Metadata: parentKey (issue the comment belongs to), 
  │             epicLink (inherited from parent issue),
  │             commentAuthor, containsDecision (flagged),
  │             commentType (comment/review/approval/verification)
  └── Purpose: DECISION & RATIONALE — included only when relevant to the question
```

### 4.2 ZVec.NET Document POCO

```csharp
public sealed class JiraDocChunk
{
    [ZVecId]
    public string Id { get; set; } = "";
    // Format: "{issueType}_{key}" or "comment_{key}_{commentIndex}"
    // Examples: "epic_SPARK-56664", "story_SPARK-56962", 
    //           "bug_SPARK-8469", "subtask_SPARK-51530",
    //           "comment_SPARK-8469_0", "comment_SPARK-8469_1"

    // ── Vector Field ──
    [ZVecVector(768, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    // ── Tier ──
    public int Tier { get; set; }               // 0=Epic/Umbrella, 1=Issue, 2=Sub-task, 3=Comment

    // ── Issue Identity ──
    public string Key { get; set; } = "";       // "SPARK-56664", "SPARK-8469"
    public string IssueType { get; set; } = ""; // "Epic","Story","Umbrella","Bug",
                                                 // "Improvement","Task","New Feature",
                                                 // "Sub-task","Comment"

    // ── Hierarchy Navigation (THE CRITICAL FIELDS) ──
    public string EpicLink { get; set; } = "";    // Parent Epic key (from customfield_12311120)
                                                    // For Tier 0 Epics: empty or self-referencing
                                                    // For Tier 1 Issues: the Epic they belong to
                                                    // For Tier 2/3: inherited from parent issue
    public string ParentKey { get; set; } = "";    // For Sub-tasks: parent issue key
                                                    // For Comments: the issue they comment on
                                                    // For Tier 0/1 Issues: empty

    // ── Content ──
    public string Summary { get; set; } = "";      // Issue title / Epic name
    public string Description { get; set; } = "";   // Full description text
    public string Status { get; set; } = "";         // "Open","Resolved","Closed","In Progress"

    // ── Categorization ──
    public string Components { get; set; } = "";    // semicolon-separated: "SQL;Spark Core"
    public string Labels { get; set; } = "";         // semicolon-separated
    public string FixVersions { get; set; } = "";    // semicolon-separated: "4.0.0;4.1.0"
    public string Priority { get; set; } = "";       // "Major","Minor","Critical","Blocker"
    public string Assignee { get; set; } = "";       // displayName or "Unassigned"

    // ── Comment-Specific (Tier 3 only) ──
    public string CommentAuthor { get; set; } = "";
    public bool ContainsDecision { get; set; }        // Flagged if comment contains decision keywords:
                                                       // "decided", "agreed", "because", "rationale",
                                                       // "approved", "we will", "let's go with"

    // ── Embedding Source Text (what was sent to LM Studio for embedding) ──
    // This is NOT stored in ZVec.NET but logged for debugging
    // Format varies by tier:
    //   Tier 0: "{Summary}\n{Description}"
    //   Tier 1: "{Key}: {Summary}\n{Description}\nType: {IssueType}, Status: {Status}"
    //   Tier 2: "{Key}: {Summary}\n{Description}\nParent: {ParentKey}"
    //   Tier 3: "{CommentAuthor} on {ParentKey}: {CommentBody}"
}
```

### 4.3 Embedding Text Composition

What text is sent to LM Studio's embedding endpoint varies by tier — this affects retrieval quality:

| Tier | Embedding Text Format | Why |
|---|---|---|
| 0 (Epic) | `"{EpicName}: {Description}"` | Epics need to match on business intent, not issue key |
| 1 (Issue) | `"SPARK-{ID}: {Summary}\n{Description}\nType: {IssueType}"` | Include type so "validation bug" and "validation improvement" are distinguishable |
| 2 (Sub-task) | `"SPARK-{ID}: {Summary}\n{Description}"` | Sub-tasks inherit context from parent |
| 3 (Comment) | `"On SPARK-{ParentKey}: {CommentAuthor} said: {CommentBody}"` | Comments must match on decision content, not just the issue they're attached to |

**For Scenario B (new requirement)**: The user's requirement text is embedded in the same format pattern as Tier 1 issues, so it naturally aligns with existing stories and bugs in vector space.

### 4.4 Decision Flag Detection

Comments are flagged as `ContainsDecision=true` if they contain any of these patterns:

```csharp
private static readonly string[] DecisionKeywords = {
    "decided", "decision", "agreed", "agree", "approved", "approve",
    "because", "rationale", "reason", "we will", "let's go with",
    "let us go with", "chosen", "chose", "preferred", "preference",
    "after discussion", "conclusion", "resolved to", "determined"
};

public bool DetectDecision(string commentBody)
{
    var lower = commentBody.ToLowerInvariant();
    return DecisionKeywords.Any(kw => lower.Contains(kw));
}
```

This is a simple heuristic — can be upgraded to LLM-based classification in production.

---

## 5. Retrieval & Navigation Engine

### 5.1 Intent Classification

**Current (shipped):** hybrid classifier behind `IIntentClassifier`.

1. **Heuristic fast path** (`IntentClassifier`): Jira key in focus → decision phrases → requirement phrases → `GeneralQuestion`.
2. **LLM JSON classify** (`HybridIntentClassifier`): only when the heuristic returns `GeneralQuestion` (ambiguous paraphrase). Timeout / parse failure → stay on `GeneralQuestion`.
3. Intent is classified **once** per chat request, passed into `NavigateAsync` and into intent-aware prompts (`BuildSystemPrompt` / `BuildUserPrompt`).

```csharp
public enum QueryIntent
{
    AssignedIssue,      // Scenario A: known ticket number
    NewRequirement,     // Scenario B: describes need, no ticket
    DecisionRationale,  // Scenario C: asks why/rationale
    GeneralQuestion     // Fallback: broad question about the project
}
```

Heuristic sketch (fast path only):

```csharp
public static QueryIntent ClassifyIntent(string userInput)
{
    // Check for Jira issue key pattern (SPARK-NNNNN, DEV-NNN, etc.)
    if (Regex.IsMatch(userInput, @"[A-Z]+-\d+", RegexOptions.IgnoreCase))
        return QueryIntent.AssignedIssue;

    // Check for decision/rationale question
    if (userInput.ContainsAny("why", "decided", "rationale", "reason for",
                               "what was the decision", "how did they choose"))
        return QueryIntent.DecisionRationale;

    // Check for new requirement indicators
    if (userInput.ContainsAny("I need to", "I want to add", "we should add",
                               "I have to implement", "new feature", "add validation",
                               "when", "should", "must", "requirement"))
        return QueryIntent.NewRequirement;

    return QueryIntent.GeneralQuestion;
}
```

### 5.2 Retrieval Strategy per Intent

#### AssignedIssue (Scenario A)

```csharp
public async Task<NavigatedContext> NavigateFromAssignedIssue(string issueKey)
{
    // Phase 1: Direct fetch — no vector search needed
    var issueChunk = _vectorStore.Fetch(issueKey);
    if (issueChunk == null)
        return NavigatedContext.NotFound(issueKey);

    // Phase 2: Navigate hierarchy using metadata (NOT vector search)
    var context = new NavigatedContext { CentralIssue = issueChunk };

    // UP: Parent Epic
    if (!string.IsNullOrEmpty(issueChunk.EpicLink))
    {
        context.ParentEpic = _vectorStore.Fetch(issueChunk.EpicLink);
    }

    // SIDE: Sibling stories under same Epic
    if (!string.IsNullOrEmpty(issueChunk.EpicLink))
    {
        context.SiblingIssues = _vectorStore.QueryByFilter(
            filter: p => p.EpicLink == issueChunk.EpicLink 
                     && p.Tier == 1 
                     && p.Key != issueKey
        );
    }

    // DOWN: Sub-tasks
    context.SubTasks = _vectorStore.QueryByFilter(
        filter: p => p.ParentKey == issueKey && p.Tier == 2
    );

    // DOWN: Decision comments on this issue
    context.DecisionComments = _vectorStore.QueryByFilter(
        filter: p => p.ParentKey == issueKey 
                 && p.Tier == 3 
                 && p.ContainsDecision == true
    );

    // CROSS: Related issues in same component (limited, top 5)
    if (!string.IsNullOrEmpty(issueChunk.Components))
    {
        context.CrossReferences = _vectorStore.Query(
            p => p.Embedding, issueChunk.Embedding,
            topK: 5,
            filter: p => p.Components.Contains(issueChunk.Components.Split(';')[0])
                     && p.Tier == 1
                     && p.Key != issueKey
        );
    }

    return context;
}
```

#### NewRequirement (Scenario B)

```csharp
public async Task<NavigatedContext> NavigateFromNewRequirement(string requirementText)
{
    // Phase 1: Embed the requirement
    var requirementVec = await _embeddingService.Embed(requirementText);

    // Phase 2: Multi-tier HNSW search — no filter, search everything
    var allHits = _vectorStore.Query(
        p => p.Embedding, requirementVec,
        topK: 20  // Get more results for clustering
    );

    // Phase 3: Cluster results by EpicLink
    var clusters = allHits
        .GroupBy(h => h.Record.EpicLink)
        .Where(g => !string.IsNullOrEmpty(g.Key))
        .OrderByDescending(g => g.Count())
        .Take(3)  // Top 3 most relevant feature areas
        .ToList();

    // Also include hits without EpicLink (standalone Bugs/Improvements)
    var standaloneHits = allHits
        .Where(h => string.IsNullOrEmpty(h.Record.EpicLink) && h.Record.Tier == 1)
        .Take(5)
        .ToList();

    // Phase 4: Build navigated context from clusters
    var context = new NavigatedContext { RequirementText = requirementText };

    foreach (var cluster in clusters)
    {
        // Fetch the Epic header for this cluster
        var epic = _vectorStore.Fetch(cluster.Key);
        if (epic != null)
        {
            context.RelatedEpics.Add(epic);

            // Get all sibling issues under this Epic
            var siblings = _vectorStore.QueryByFilter(
                filter: p => p.EpicLink == cluster.Key && p.Tier == 1
            );
            context.RelatedStories.AddRange(siblings);
        }

        // Get decision comments from cluster hits
        var clusterDecisionComments = cluster
            .Where(h => h.Record.Tier == 3 && h.Record.ContainsDecision)
            .Select(h => h.Record)
            .Take(3)
            .ToList();
        context.DecisionComments.AddRange(clusterDecisionComments);
    }

    context.StandaloneRelatedIssues = standaloneHits.Select(h => h.Record).ToList();

    return context;
}
```

#### DecisionRationale (Scenario C)

```csharp
public async Task<NavigatedContext> NavigateFromDecisionQuestion(string question)
{
    // Phase 1: Embed the question
    var questionVec = await _embeddingService.Embed(question);

    // Phase 2: Targeted search — decision comments only
    var decisionHits = _vectorStore.Query(
        p => p.Embedding, questionVec,
        topK: 10,
        filter: p => p.ContainsDecision == true && p.Tier == 3
    );

    // Phase 3: Navigate UP from each decision comment
    var context = new NavigatedContext();

    foreach (var hit in decisionHits.Take(5))
    {
        var comment = hit.Record;

        // UP: Parent issue the comment belongs to
        var parentIssue = _vectorStore.Fetch(comment.ParentKey);
        if (parentIssue != null)
            context.ParentIssues.Add(parentIssue);

        // UP-UP: Epic of the parent issue
        if (!string.IsNullOrEmpty(comment.EpicLink))
        {
            var epic = _vectorStore.Fetch(comment.EpicLink);
            if (epic != null && !context.ParentEpics.Any(e => e.Key == epic.Key))
                context.ParentEpics.Add(epic);
        }

        context.DecisionComments.Add(comment);
    }

    return context;
}
```

### 5.3 Context Assembly (What Goes Into the LLM Prompt)

The Context Builder decides **what depth to include** based on scenario:

```csharp
public string BuildRagContext(NavigatedContext nav, QueryIntent intent)
{
    var sb = new StringBuilder();

    switch (intent)
    {
        case QueryIntent.AssignedIssue:
            // Full hierarchy: Epic → Siblings → Issue → Comments
            AppendEpicHeader(sb, nav.ParentEpic);
            AppendSiblingSummary(sb, nav.SiblingIssues);
            AppendIssueDetail(sb, nav.CentralIssue);
            AppendDecisionComments(sb, nav.DecisionComments);
            AppendCrossReferences(sb, nav.CrossReferences, maxCount: 3);
            break;

        case QueryIntent.NewRequirement:
            // Landscape view: Related Epics → Closest Stories → Relevant Bugs → Patterns
            sb.AppendLine("No exact match found for this requirement.");
            sb.AppendLine("Here is the relevant landscape of existing work:");
            foreach (var epic in nav.RelatedEpics)
                AppendEpicHeader(sb, epic);
            AppendRelatedStories(sb, nav.RelatedStories, maxCount: 10);
            AppendStandaloneIssues(sb, nav.StandaloneRelatedIssues, maxCount: 5);
            AppendDecisionPatterns(sb, nav.DecisionComments, maxCount: 3);
            break;

        case QueryIntent.DecisionRationale:
            // Decision-focused: Epic header → Issue context → Decision comment
            foreach (var epic in nav.ParentEpics)
                AppendEpicHeader(sb, epic);
            foreach (var issue in nav.ParentIssues)
                AppendIssueDetail(sb, issue);
            AppendDecisionComments(sb, nav.DecisionComments);
            break;
    }

    return sb.ToString();
}
```

**Context template for LLM**:

```
System Prompt:
You are a Project Docs Navigator. Your job is to guide the developer 
through the project documentation hierarchy — NOT to just answer questions.

For assigned issues: Show the business context (Epic), the scope 
(sibling stories), and any relevant decisions or open items.

For new requirements: Show the landscape of related existing work, 
suggest which Epics/Stories are most relevant, and note what patterns 
or decisions from similar work might inform their approach.

For decision questions: Show the decision rationale from comments, 
with the surrounding context of what triggered the discussion.

Always structure your response with clear sections and use the 
hierarchy information provided.

---

User Prompt:
{assembled_context}

{original_user_question}
```

---

## 6. Tech Stack

| Component | Technology | Version | Role |
|---|---|---|---|
| **Language** | C# / .NET | net10.0 | Application runtime |
| **Vector DB** | ZVec.NET (NuGet) | 1.0.0-beta.2+zvec.0.5.1 | HNSW vector storage, similarity search, metadata filtering |
| **Embedding Model** | Configurable (default: nomic-embed-text-v1.5 via LM Studio) | GGUF Q4_0 | 768-dim text embeddings, configurable via appsettings.json or UI |
| **Chat LLM** | Configurable (default: Qwen2.5-7B-Instruct via LM Studio) | GGUF | RAG inference, configurable via appsettings.json or UI |
| **LM Studio API** | OpenAI-compatible REST | localhost:1234/v1 | Embedding + Chat endpoints |
| **Data Source** | Apache Spark on Apache Jira | REST API v2 | Project docs ingestion |
| **HTTP Client** | System.Net.Http | Built-in | LM Studio API calls, Jira API calls |
| **Streaming** | SSE (Server-Sent Events) | HTTP | Real-time LLM response streaming from API → UI |
| **Architecture** | Separate API + UI projects | net10.0 | ZVec in API only; UI is thin Blazor Server client via HTTP + SSE |

### LM Studio Configuration

```json
// appsettings.json
{
  "LmStudio": {
    "BaseUrl": "http://localhost:1234/v1",
    "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5",
    "ChatModel": "lmstudio-community/Qwen2.5-7B-Instruct-GGUF",
    "EmbeddingDimensions": 768,
    "ChatTemperature": 0.3,
    "ChatMaxTokens": -1
  },
  "Jira": {
    "BaseUrl": "https://issues.apache.org/jira/rest/api/2",
    "ProjectKey": "SPARK",
    "MaxResultsPerRequest": 100
  },
  "ZVec": {
    "CollectionPath": "./data/spark-docs-zvec",
    "LogLevel": "Warn",
    "EnableMmap": true
  },
  "Ingestion": {
    "MaxEpics": 74,
    "MaxStories": 138,
    "MaxUmbrellas": 506,
    "MaxBugs": 500,       // Limit to bugs with > 2 comments
    "MaxImprovements": 500, // Limit to improvements with > 2 comments
    "MaxCommentsPerIssue": 10 // Only embed most relevant comments
  }
}
```

### LM Studio API Integration (OpenAI-compatible)

```csharp
// Embedding call
POST http://localhost:1234/v1/embeddings
{
  "model": "text-embedding-nomic-embed-text-v1.5",
  "input": ["SPARK-56664: Streaming Shuffle Coordination\n...description..."]
}
→ Response: { "data": [{ "embedding": [0.0023, -0.0094, ...] }] }

// Chat call
POST http://localhost:1234/v1/chat/completions
{
  "model": "lmstudio-community/Qwen2.5-7B-Instruct-GGUF",
  "messages": [
    { "role": "system", "content": "You are a Project Docs Navigator..." },
    { "role": "user", "content": "{assembled_context}\n\n{user_question}" }
  ],
  "temperature": 0.3,
  "max_tokens": -1,
  "stream": false
}
→ Response: { "choices": [{ "message": { "content": "..." } }] }
```

---

## 7. Ingestion Pipeline

### 7.1 Data Fetching Strategy

Not all 57,850 issues need to be ingested. For the POC, we fetch a representative subset that covers the full hierarchy:

```
Phase 1: Full Hierarchy Top
  → ALL 74 Epics (complete)
  → ALL 138 Stories (complete)
  → ALL 506 Umbrellas (complete)

Phase 2: Rich Issues (with comments)
  → Top 500 Bugs with > 2 comments (sorted by comment count locally)
  → Top 500 Improvements with > 2 comments
  → Top 100 Tasks with > 2 comments
  → Top 100 Sub-tasks from the issues above

Phase 3: Comments
  → All comments from Phase 2 issues
  → Apply ContainsDecision flag detection
  → Skip trivial comments (auto-generated PR links, etc.)

Total estimated chunks: ~2,000-3,000
```

### 7.2 Ingestion Flow

```
┌──────────────────┐
│  Jira REST API    │
│  Fetch issues     │
│  by type + filter │
└────────────┬─────┘
             │
┌────────────▼──────────────┐
│  Hierarchy Parser          │
│                            │
│  For each issue:           │
│  - Extract EpicLink        │
│  - Extract subtask links   │
│  - Extract issuelinks      │
│  - Extract comments        │
│  - Flag decision comments  │
│  - Build parent/child map  │
└────────────┬──────────────┘
             │
┌────────────▼──────────────┐
│  Chunk Creator             │
│                            │
│  Creates JiraDocChunk for: │
│  - 1 Tier 0 per Epic      │
│  - 1 Tier 1 per Issue     │
│  - 1 Tier 2 per Sub-task  │
│  - 1 Tier 3 per Comment   │
│                            │
│  Composes embedding text   │
│  per tier format rules     │
└────────────┬──────────────┘
             │
┌────────────▼──────────────┐
│  Embedding Batch           │
│                            │
│  LM Studio /v1/embeddings │
│  Batch: 50 texts per call │
│  Model: nomic-embed-text  │
│  → float[768] per chunk   │
└────────────┬──────────────┘
             │
┌────────────▼──────────────┐
│  ZVec.NET Insert           │
│                            │
│  Insert each JiraDocChunk │
│  into ZVec.NET collection │
│                            │
│  HNSW index builds         │
│  automatically on insert   │
│  M=32, EfConstruction=256 │
│  Metric: Cosine            │
└───────────────────────────┘
```

### 7.3 Jira API Pagination & Rate Limiting

```csharp
// Paginated fetching with rate limiting
public async Task<List<JiraIssue>> FetchAllIssues(string jql, int maxTotal)
{
    var allIssues = new List<JiraIssue>();
    int startAt = 0;
    int maxResults = 100; // Jira API max per request

    while (startAt < maxTotal)
    {
        var response = await _httpClient.GetAsync(
            $"search?jql={jql}&startAt={startAt}&maxResults={maxResults}&expand=comments"
        );

        var result = await response.Content.ReadFromJsonAsync<JiraSearchResult>();
        allIssues.AddRange(result.Issues);

        startAt += maxResults;

        // Rate limiting: 1 request per second
        await Task.Delay(1000);
    }

    return allIssues;
}
```

---

## 8. Project Structure

```
ZVecRAG.POC/
├── src/
│   ├── ZVecRAG.Core/
│   │   ├── Models/
│   │   │   ├── JiraDocChunk.cs               // ZVec.NET POCO with hierarchy metadata
│   │   │   ├── JiraApiModels.cs              // Jira REST API response DTOs
│   │   │   ├── LmStudioModels.cs             // Embedding + Chat request/response DTOs
│   │   │   ├── NavigatedContext.cs            // Assembled hierarchy context model
│   │   │   └── QueryIntent.cs                // Intent enum + classifier
│   │   ├── Services/
│   │   │   ├── JiraFetcherService.cs          // Fetch from issues.apache.org/jira REST API
│   │   │   ├── HierarchyParserService.cs      // Parse Epic Links, sub-tasks, issuelinks
│   │   │   ├── ChunkingService.cs             // 4-tier chunk creation + embedding text composition
│   │   │   ├── DecisionDetector.cs            // Flag ContainsDecision on comments
│   │   │   ├── EmbeddingService.cs            // LM Studio /v1/embeddings calls
│   │   │   ├── VectorStoreService.cs          // ZVec.NET init, insert, query, fetch, filter
│   │   │   ├── NavigationEngine.cs            // ⭐ THE CORE: Navigate UP/SIDE/DOWN/CROSS per scenario
│   │   │   ├── ContextBuilderService.cs       // Hierarchical context assembly per intent
│   │   │   └───────── ChatService.cs              // LM Studio /v1/chat/completions
│   │   └───────── Configuration/
│   │       └───────── AppSettings.cs
│   ├── ZVecRAG.Console/
│   │   ├── Program.cs                          // Interactive chat loop
│   │   └───────── Commands/
│   │       ├── IngestCommand.cs                // Fetch + chunk + embed + store
│   │       ├── ChatCommand.cs                  // Interactive chat (all 3 scenarios)
│   │       ├── StatsCommand.cs                 // Show vector store statistics
│   │       └───────── ResetCommand.cs              // Clear and rebuild
│   └───────── ZVecRAG.Tests/
│       ├── NavigationEngineTests.cs
│       ├── HierarchyParserTests.cs
│       ├── DecisionDetectorTests.cs
│       └───────── ChunkingTests.cs
├── data/
│   └── spark-docs-zvec/                        // ZVec.NET persistent storage
├── appsettings.json
└───────── README.md
```

---

## 9. Demo Flow

### 9.1 Demo Script

```
═══ DEMO: Project Docs Smart Mind ═══

Step 0: Ingestion
  > ingest
  Fetching 74 Epics... ✅
  Fetching 138 Stories... ✅
  Fetching 506 Umbrellas... ✅
  Fetching 500 Bugs (with comments)... ✅
  Fetching 500 Improvements (with comments)... ✅
  Chunking into 4 tiers... ✅ (2,847 chunks created)
  Embedding via LM Studio... ✅ (nomic-embed-text-v1.5)
  Inserting into ZVec.NET... ✅ (HNSW M=32, Cosine)
  
  Vector store ready: 2,847 chunks across 4 tiers
  Tier 0: 580 (Epics + Umbrellas)
  Tier 1: 1,238 (Stories + Bugs + Improvements + Tasks)
  Tier 2: 153 (Sub-tasks)
  Tier 3: 876 (Comments, 142 flagged as decisions)

─────────────────────────────────────────

Step 1: Scenario A — Assigned Issue
  > I got assigned SPARK-57337

  Intent: AssignedIssue → Direct fetch + hierarchy navigation

  Smart Mind Response:
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  📌 EPIC: SPARK-56664 — Streaming Shuffle Coordination
     This feature adds real-time shuffle coordination between
     Spark executors to reduce latency in streaming pipelines.
     Status: In Progress | Component: Spark Core

  📋 YOUR ISSUE: SPARK-57337 (Part 3.5)
     "Add streaming shuffle shared transport and error plumbing"
     Status: Resolved | Priority: Major

  📋 SIBLING WORK (7 stories in this Epic):
     ✅ Part 1: Wire Protocol (SPARK-56674) — Resolved
     ✅ Part 2: Output Tracker (SPARK-56962) — Resolved
     ✅ Part 3.5: Error plumbing (SPARK-57337) — Resolved ← YOU
     ✅ Part 4: Server-side Netty (SPARK-57229) — Resolved
     ✅ Part 5: Client-side Netty (SPARK-57230) — Resolved
     ⬜ Part 6: DAGScheduler (SPARK-57231) — Open ⚠️
     ✅ Part 7: E2E testing (SPARK-57232) — Resolved

  ⚠️ NOTE: Part 6 (SPARK-57231) is still Open — your 
     work may need to coordinate with it.
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

─────────────────────────────────────────

Step 2: Scenario B — New Requirement
  > I need to add validation for ANSI mode when casting 
    strings to numbers — it should throw an error instead 
    of returning null

  Intent: NewRequirement → Vector search + landscape navigation

  Smart Mind Response:
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  No exact story exists for this requirement. Here is the 
  relevant landscape:

  📌 EPIC: SPARK-49577 — Improve ANSI by Default
     ANSI mode was turned on by default from Spark 4.0.0. 
     This epic concerns all gaps and issues to improve Spark 
     with ANSI mode on by default.
     Component: SQL | Status: Open

  📋 RELATED WORK under this Epic:
     (No children stories — this Epic is still open/planning)

  🐛 RELATED BUGS (closest matches):
     • SPARK-XXXXX: ANSI mode casting returns null instead 
       of error for decimal overflow (Resolved)
     • SPARK-XXXXX: String-to-number conversion should fail 
       in ANSI mode (Open) ← DIRECTLY RELEVANT

  💬 PATTERN FROM DECISIONS:
     "We decided that in ANSI mode, invalid casts should 
       throw AnalysisException instead of returning null, 
       consistent with the SQL standard behavior."

  💡 SUGGESTION: Your requirement aligns with Epic 
     SPARK-49577 (ANSI by default). Consider creating a 
     Story under this Epic for the specific validation 
     case you described. Bug SPARK-XXXXX may already 
     cover part of what you need.
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

─────────────────────────────────────────

Step 3: Scenario C — Decision Rationale
  > Why did Spark decide to use ANSI mode as default in 4.0?

  Intent: DecisionRationale → Targeted comment search + context

  Smart Mind Response:
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  📌 EPIC: SPARK-49577 — Improve ANSI by Default
     The broader context is making Spark more SQL-standard 
     compliant by default.

  💬 DECISION (from comment on SPARK-XXXXX by @cloud_fan):
     "We decided to enable ANSI mode by default in Spark 4.0 
       because: (1) most enterprise users already run in ANSI 
       mode, (2) the SQL standard requires strict behavior, 
       and (3) returning null for invalid casts was a 
       compatibility trap that confused users."

  📋 TRIGGERING ISSUE: SPARK-XXXXX — 
     "ANSI mode should be the default behavior"
     Status: Resolved | Resolution: Fixed
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

═══ DEMO END ═══
```

### 9.2 Key Demo Talking Points

| Point | What to Show |
|---|---|
| **The Pain** | Show manually navigating 10+ Jira pages for context (45 min) |
| **The Solution** | Show Smart Mind assembling full hierarchy in 1 interaction |
| **Scenario A** | Assigned ticket → instant hierarchy navigation |
| **Scenario B** | New requirement → finds relevant landscape even with no existing ticket |
| **Scenario C** | Decision question → finds the specific comment with rationale |
| **ZVec.NET Power** | Show HNSW query speed (~2-3ms), metadata filtering, direct fetch |
| **Local AI** | Everything runs locally — no cloud, no API keys, no privacy concerns |
| **Production Potential** | "This could be a Jira/GitLab plugin — attach to your org's docs server" |

---

## 10. Production Path (Beyond POC)

The POC proves the concept. The production product would be:

| POC (original) | Current / production direction |
|---|---|
| Apache Spark (public) | Organization's internal Jira/GitLab |
| Console app | Jira plugin / GitLab app / VS Code extension / Web UI |
| Simple keyword intent classifier | **Hybrid intent** (heuristic fast path + LLM JSON on ambiguous) — shipped |
| Simple decision keyword detection | LLM-based decision extraction (still future) |
| Static ingestion | Live sync with project docs server |
| One project | Multi-project support |
| English only | Multi-language |
| No user auth | Org auth integration |

**Revenue model**: Plugin subscription — organizations pay per Jira/GitLab instance to attach the Smart Mind navigator to their project docs server.

---

## 11. Implementation Milestones

| Milestone | Tasks | Est. Time |
|---|---|---|
| **M1: Foundation** | Project setup, ZVec.NET init, LM Studio connection, Jira API client | 1-2 days |
| **M2: Ingestion** | Jira fetcher, hierarchy parser, chunking service, decision detector, batch embedding, ZVec.NET insert | 2-3 days |
| **M3: Scenario A** | Intent classifier (assigned issue), Navigation Engine (direct fetch + hierarchy), Context Builder, Chat integration | 2-3 days |
| **M4: Scenario B** | Multi-tier HNSW search, cluster-by-epic logic, landscape context assembly | 2-3 days |
| **M5: Scenario C** | Decision filter search, navigate-UP from comments, decision context assembly | 1-2 days |
| **M6: Demo Polish** | Console UI formatting, demo script testing, edge cases, README | 1-2 days |
| **Total** | | **10-15 days** |

---

## 12. Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Many Epics have 0 comments | Scenario C may have limited decision data | Focus on Bugs/Improvements with rich comment history |
| Jira API rate limiting | Slow ingestion for large datasets | Paginate with delays; limit to ~3,000 chunks for POC |
| nomic-embed-text context limit (8192) | Very long descriptions may truncate | Chunk large descriptions into 2 pieces |
| ZVec.NET is beta (1.0.0-beta.2) | API may change | Pin version; test thoroughly |
| LM Studio model availability | Different hardware may need different models | Support configurable models in appsettings |
| New requirement matching | May find unrelated results if requirement is vague | Use cluster-by-epic to reduce noise; show only top 2-3 clusters |
| Comment quality varies | Many comments are auto-generated (PR links) | Filter out "has created a pull request" pattern comments during ingestion |

---

*End of POC Plan*
