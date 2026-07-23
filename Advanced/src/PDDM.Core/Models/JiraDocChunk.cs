using ZVec.NET.Mapping;
using PDDM.Core.Constants;
using PDDM.Shared.Constants;
using ZVec.NET;

namespace PDDM.Core.Models;

/// <summary>
/// Single ZVec.NET document for one Jira project-doc chunk.
/// Id format: "{prefix}{key}" or "comment_{key}_{index}".
/// </summary>
[ZVecCollection(PddmDefaults.CollectionName)]
public sealed class JiraDocChunk
{
    /// <summary>Unique document id (ZVec identity).</summary>
    public string Id { get; set; } = "";

    /// <summary>Dense embedding; dimension locked to <see cref="PddmDefaults.EmbeddingDimensions"/>.</summary>
    [ZVecVector(PddmDefaults.EmbeddingDimensions, Metric = ZVecMetricType.Cosine, M = PddmDefaults.HnswM, EfConstruction = PddmDefaults.HnswEfConstruction)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>Tier: 0 Epic/Umbrella, 1 Issue, 2 Sub-task, 3 Comment.</summary>
    public int Tier { get; set; }

    public string IssueType { get; set; } = "";
    public string Key { get; set; } = "";
    public string EpicLink { get; set; } = "";
    public string ParentKey { get; set; } = "";
    public string UmbrellaLink { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string Components { get; set; } = "";
    public string Labels { get; set; } = "";
    public string FixVersions { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string CommentAuthor { get; set; } = "";
    public bool ContainsDecision { get; set; }
}
