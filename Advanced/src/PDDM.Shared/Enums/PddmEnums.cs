namespace PDDM.Shared;

/// <summary>Query intent classification for the three PDDM scenarios plus fallback.</summary>
public enum QueryIntent
{
    /// <summary>Known Jira issue key (Scenario A).</summary>
    AssignedIssue = 0,

    /// <summary>Free-text requirement with no ticket (Scenario B).</summary>
    NewRequirement = 1,

    /// <summary>Decision / rationale question (Scenario C).</summary>
    DecisionRationale = 2,

    /// <summary>Fallback — routed to semantic search like Scenario B.</summary>
    GeneralQuestion = 3
}

/// <summary>Document tier in the four-tier chunk model.</summary>
public enum DocTier
{
    /// <summary>Epic or Umbrella.</summary>
    EpicOrUmbrella = 0,

    /// <summary>Story, Bug, Improvement, Task, New Feature.</summary>
    Issue = 1,

    /// <summary>Sub-task.</summary>
    SubTask = 2,

    /// <summary>Comment / discussion.</summary>
    Comment = 3
}
