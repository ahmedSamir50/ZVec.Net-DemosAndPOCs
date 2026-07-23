using System.Text.Json.Serialization;

namespace PDDM.Core.Models.JiraApi;

/// <summary>Jira search response.</summary>
public sealed class JiraSearchResult
{
    public int StartAt { get; set; }
    public int MaxResults { get; set; }
    public int Total { get; set; }
    public List<JiraIssue> Issues { get; set; } = [];
}

/// <summary>Jira issue envelope.</summary>
public sealed class JiraIssue
{
    public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public JiraIssueFields Fields { get; set; } = new();
}

/// <summary>Jira issue fields used by PDDM.</summary>
public sealed class JiraIssueFields
{
    public JiraIssueType? Issuetype { get; set; }
    public string Summary { get; set; } = "";
    public string? Description { get; set; }
    public JiraStatus? Status { get; set; }
    public JiraPriority? Priority { get; set; }
    public List<JiraComponent> Components { get; set; } = [];
    public List<JiraVersion> FixVersions { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public JiraUser? Assignee { get; set; }

    [JsonPropertyName("customfield_12311120")]
    public string? EpicLink { get; set; }

    public JiraParentIssue? Parent { get; set; }
    public List<JiraSubtask> Subtasks { get; set; } = [];
    public List<JiraIssueLink> Issuelinks { get; set; } = [];
    public JiraComments? Comment { get; set; }
}

public sealed class JiraIssueType
{
    public string Name { get; set; } = "";
    public bool Subtask { get; set; }
}

public sealed class JiraStatus { public string Name { get; set; } = ""; }
public sealed class JiraPriority { public string Name { get; set; } = ""; }
public sealed class JiraComponent { public string Name { get; set; } = ""; }
public sealed class JiraVersion { public string Name { get; set; } = ""; }
public sealed class JiraUser { public string DisplayName { get; set; } = ""; }
public sealed class JiraParentIssue { public string Key { get; set; } = ""; }
public sealed class JiraSubtask { public string Key { get; set; } = ""; public string Summary { get; set; } = ""; }
public sealed class JiraIssueLink
{
    public JiraLinkedIssue? OutwardIssue { get; set; }
    public JiraLinkedIssue? InwardIssue { get; set; }
}
public sealed class JiraLinkedIssue
{
    public string Key { get; set; } = "";
    public JiraIssueType? Issuetype { get; set; }
}
public sealed class JiraComments { public List<JiraComment> Comments { get; set; } = []; }
public sealed class JiraComment
{
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public JiraUser? Author { get; set; }
}
