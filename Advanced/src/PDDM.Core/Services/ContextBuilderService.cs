using System.Text;
using PDDM.Core.Abstractions;
using PDDM.Core.Constants;
using PDDM.Core.Helpers;
using PDDM.Core.Models;
using PDDM.Shared;
using PDDM.Shared.Constants;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class ContextBuilderService : IContextBuilder
{
    /// <inheritdoc />
    public string Build(NavigatedContext navigation, QueryIntent intent)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (!string.IsNullOrEmpty(navigation.AssembledContext) && navigation.CentralIssue is null
            && navigation.RelatedEpics.Count == 0 && navigation.DecisionComments.Count == 0
            && navigation.ParentIssues.Count == 0 && navigation.StandaloneRelatedIssues.Count == 0)
        {
            return navigation.AssembledContext;
        }

        var sb = new StringBuilder();
        switch (intent)
        {
            case QueryIntent.AssignedIssue:
                AppendEpic(sb, navigation.ParentEpic);
                AppendList(sb, "Sibling work", navigation.SiblingIssues, PddmDefaults.ContextMaxRelatedStories);
                AppendIssue(sb, "Central issue", navigation.CentralIssue);
                AppendList(sb, "Decision comments", navigation.DecisionComments, PddmDefaults.ContextMaxDecisionComments);
                AppendList(sb, "Cross references", navigation.CrossReferences, PddmDefaults.ContextMaxCrossRefs);
                break;

            case QueryIntent.NewRequirement:
            case QueryIntent.GeneralQuestion:
                sb.AppendLine("No exact match found for this requirement.");
                sb.AppendLine("Relevant landscape of existing work:");
                foreach (var epic in navigation.RelatedEpics.Take(PddmDefaults.DefaultClusterCount))
                    AppendEpic(sb, epic);
                AppendList(sb, "Related stories/issues", navigation.RelatedStories, PddmDefaults.ContextMaxRelatedStories);
                AppendList(sb, "Standalone related issues", navigation.StandaloneRelatedIssues, PddmDefaults.DefaultStandaloneHits);
                AppendList(sb, "Decision patterns", navigation.DecisionComments, PddmDefaults.ContextMaxDecisionComments);
                if (navigation.RelatedEpics.Count == 0
                    && navigation.RelatedStories.Count == 0
                    && navigation.StandaloneRelatedIssues.Count == 0
                    && navigation.DecisionComments.Count == 0)
                {
                    sb.AppendLine("No related tickets in the index yet. Run Ingestion (with ANSI seed) and retry.");
                }
                break;

            case QueryIntent.DecisionRationale:
                foreach (var epic in navigation.ParentEpics)
                    AppendEpic(sb, epic);
                foreach (var issue in navigation.ParentIssues)
                    AppendIssue(sb, "Parent issue", issue);
                AppendList(sb, "Decision comments", navigation.DecisionComments, PddmDefaults.ContextMaxDecisionComments);
                if (navigation.ParentEpics.Count == 0
                    && navigation.ParentIssues.Count == 0
                    && navigation.DecisionComments.Count == 0)
                {
                    sb.AppendLine("No decision comments or related issues found in the index.");
                    sb.AppendLine($"Expected seed: {GoldenDemoSeedKeys.AnsiDefaultDecision} — Url: {BrowseUrl(GoldenDemoSeedKeys.AnsiDefaultDecision)}");
                    sb.AppendLine("Run Ingestion and retry.");
                }
                break;
        }

        return sb.ToString();
    }

    private static string BrowseUrl(string key)
        => string.IsNullOrWhiteSpace(key) ? "" : string.Format(SharedPddmDefaults.JiraBrowseUrlFormat, key);

    private static void AppendEpic(StringBuilder sb, JiraDocChunk? epic)
    {
        if (epic is null) return;
        sb.AppendLine($"EPIC: {epic.Key} — {epic.Summary}");
        sb.AppendLine($"Url: {BrowseUrl(epic.Key)}");
        sb.AppendLine(TextTruncator.Truncate(epic.Description, PddmDefaults.ContextMaxDescriptionChars));
        sb.AppendLine($"Status: {epic.Status} | Components: {epic.Components}");
        sb.AppendLine();
    }

    private static void AppendIssue(StringBuilder sb, string title, JiraDocChunk? issue)
    {
        if (issue is null) return;
        sb.AppendLine($"{title}: {issue.Key} ({issue.IssueType}) — {issue.Summary}");
        sb.AppendLine($"Url: {BrowseUrl(issue.Key)}");
        sb.AppendLine(TextTruncator.Truncate(issue.Description, PddmDefaults.ContextMaxDescriptionChars));
        sb.AppendLine($"Status: {issue.Status} | Priority: {issue.Priority}");
        sb.AppendLine();
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyList<JiraDocChunk> items, int max)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"{title}:");
        foreach (var item in items.Take(max))
        {
            var linkKey = item.Tier == (int)DocTier.Comment ? item.ParentKey : item.Key;
            var body = item.Tier == (int)DocTier.Comment
                ? TextTruncator.Truncate(item.Description, PddmDefaults.ContextMaxDescriptionChars)
                : item.Summary;
            sb.AppendLine($"- {item.Key}: {body} [{item.Status}]");
            sb.AppendLine($"  Url: {BrowseUrl(linkKey)}");
        }

        sb.AppendLine();
    }
}
