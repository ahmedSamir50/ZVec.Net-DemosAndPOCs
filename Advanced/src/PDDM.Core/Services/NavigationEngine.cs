using PDDM.Core.Abstractions;
using PDDM.Core.Constants;
using PDDM.Core.Models;
using PDDM.Shared;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class NavigationEngine : INavigationEngine
{
    private readonly IVectorStore _vectorStore;
    private readonly IHybridIndex _hybridIndex;
    private readonly IEmbeddingService _embeddingService;
    private readonly IIntentClassifier _intentClassifier;
    private readonly IContextBuilder _contextBuilder;

    /// <summary>Creates the navigation engine.</summary>
    public NavigationEngine(
        IVectorStore vectorStore,
        IHybridIndex hybridIndex,
        IEmbeddingService embeddingService,
        IIntentClassifier intentClassifier,
        IContextBuilder contextBuilder)
    {
        _vectorStore = vectorStore;
        _hybridIndex = hybridIndex;
        _embeddingService = embeddingService;
        _intentClassifier = intentClassifier;
        _contextBuilder = contextBuilder;
    }

    /// <inheritdoc />
    public async Task<NavigatedContext> NavigateAsync(
        string userInput,
        QueryIntent intent,
        CancellationToken cancellationToken = default)
    {
        NavigatedContext context = intent switch
        {
            QueryIntent.AssignedIssue => await NavigateFromAssignedIssueAsync(
                _intentClassifier.ExtractIssueKey(userInput) ?? userInput,
                cancellationToken).ConfigureAwait(false),
            QueryIntent.DecisionRationale => await NavigateFromDecisionQuestionAsync(userInput, cancellationToken).ConfigureAwait(false),
            QueryIntent.NewRequirement => await NavigateFromNewRequirementAsync(userInput, cancellationToken).ConfigureAwait(false),
            _ => await NavigateFromNewRequirementAsync(userInput, cancellationToken).ConfigureAwait(false)
        };

        context.Intent = intent == QueryIntent.GeneralQuestion ? QueryIntent.GeneralQuestion : context.Intent;
        context.AssembledContext = _contextBuilder.Build(context, context.Intent);
        return context;
    }

    /// <inheritdoc />
    public async Task<NavigatedContext> NavigateFromAssignedIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        var context = new NavigatedContext { Intent = QueryIntent.AssignedIssue };
        var issueChunk = _hybridIndex.GetByKey(issueKey) ?? _vectorStore.FetchByKey(issueKey, includeVector: false);
        if (issueChunk is null)
            return NavigatedContext.NotFound(issueKey);

        context.CentralIssue = issueChunk;

        if (!string.IsNullOrEmpty(issueChunk.EpicLink))
        {
            context.ParentEpic = _hybridIndex.GetByKey(issueChunk.EpicLink) ?? _vectorStore.FetchByKey(issueChunk.EpicLink);
            context.SiblingIssues = _hybridIndex.GetByEpicLink(issueChunk.EpicLink)
                .Where(c => c.Tier == (int)DocTier.Issue && c.Key != issueKey)
                .ToList();
        }

        context.SubTasks = _hybridIndex.GetByParentKey(issueKey).Where(c => c.Tier == (int)DocTier.SubTask).ToList();
        context.DecisionComments = _hybridIndex.GetByParentKey(issueKey)
            .Where(c => c.Tier == (int)DocTier.Comment && c.ContainsDecision)
            .ToList();

        var crossText = $"{issueChunk.Summary}\n{issueChunk.Description}";
        var crossVec = await _embeddingService.EmbedSingleAsync(crossText, cancellationToken).ConfigureAwait(false);
        var crossHits = _vectorStore.Query(
            crossVec,
            PddmDefaults.DefaultTopKAssignedCross,
            includeVector: false,
            tier: (int)DocTier.Issue,
            excludeKey: issueKey);
        context.CrossReferences = crossHits.Select(h => h.Record).ToList();

        return context;
    }

    /// <inheritdoc />
    public async Task<NavigatedContext> NavigateFromNewRequirementAsync(string requirementText, CancellationToken cancellationToken = default)
    {
        var context = new NavigatedContext
        {
            Intent = QueryIntent.NewRequirement,
            RequirementText = requirementText
        };

        var vec = await _embeddingService.EmbedSingleAsync(requirementText, cancellationToken).ConfigureAwait(false);
        var hits = _vectorStore.Query(vec, PddmDefaults.DefaultTopKRequirement, includeVector: false);

        var clusters = hits
            .Where(h => !string.IsNullOrEmpty(h.Record.EpicLink))
            .GroupBy(h => h.Record.EpicLink)
            .OrderByDescending(g => g.Count())
            .Take(PddmDefaults.DefaultClusterCount)
            .ToList();

        foreach (var cluster in clusters)
        {
            var epic = _hybridIndex.GetByKey(cluster.Key) ?? _vectorStore.FetchByKey(cluster.Key);
            if (epic is not null)
            {
                context.RelatedEpics.Add(epic);
                context.RelatedStories.AddRange(
                    _hybridIndex.GetByEpicLink(cluster.Key).Where(c => c.Tier == (int)DocTier.Issue));
            }

            context.DecisionComments.AddRange(
                cluster.Where(h => h.Record.Tier == (int)DocTier.Comment && h.Record.ContainsDecision)
                    .Select(h => h.Record)
                    .Take(3));
        }

        context.StandaloneRelatedIssues = hits
            .Where(h => string.IsNullOrEmpty(h.Record.EpicLink) && h.Record.Tier == (int)DocTier.Issue)
            .Take(PddmDefaults.DefaultStandaloneHits)
            .Select(h => h.Record)
            .ToList();

        // Include issue-tier hits even when EpicLink clustering found nothing.
        if (context.RelatedEpics.Count == 0 && context.StandaloneRelatedIssues.Count == 0)
        {
            context.StandaloneRelatedIssues = hits
                .Where(h => h.Record.Tier == (int)DocTier.Issue || h.Record.Tier == (int)DocTier.EpicOrUmbrella)
                .Take(PddmDefaults.DefaultStandaloneHits)
                .Select(h => h.Record)
                .ToList();
        }

        return context;
    }

    /// <inheritdoc />
    public async Task<NavigatedContext> NavigateFromDecisionQuestionAsync(string question, CancellationToken cancellationToken = default)
    {
        var context = new NavigatedContext { Intent = QueryIntent.DecisionRationale };
        var vec = await _embeddingService.EmbedSingleAsync(question, cancellationToken).ConfigureAwait(false);

        var hits = _vectorStore.Query(
            vec,
            PddmDefaults.DefaultTopKDecision,
            includeVector: false,
            containsDecision: true,
            tier: (int)DocTier.Comment);

        if (hits.Count == 0)
        {
            hits = _vectorStore.Query(
                vec,
                PddmDefaults.DefaultTopKDecision,
                includeVector: false,
                tier: (int)DocTier.Comment);
        }

        if (hits.Count == 0)
        {
            hits = _vectorStore.Query(
                vec,
                PddmDefaults.DefaultTopKDecision,
                includeVector: false,
                tier: (int)DocTier.Issue);
        }

        foreach (var hit in hits.Take(PddmDefaults.ContextMaxDecisionComments))
        {
            var record = hit.Record;
            if (record.Tier == (int)DocTier.Comment)
            {
                context.DecisionComments.Add(record);
                var parent = _hybridIndex.GetByKey(record.ParentKey) ?? _vectorStore.FetchByKey(record.ParentKey);
                if (parent is not null && context.ParentIssues.All(p => p.Key != parent.Key))
                    context.ParentIssues.Add(parent);
            }
            else
            {
                if (context.ParentIssues.All(p => p.Key != record.Key))
                    context.ParentIssues.Add(record);
            }

            var epicLink = record.EpicLink;
            if (!string.IsNullOrEmpty(epicLink))
            {
                var epic = _hybridIndex.GetByKey(epicLink) ?? _vectorStore.FetchByKey(epicLink);
                if (epic is not null && context.ParentEpics.All(e => e.Key != epic.Key))
                    context.ParentEpics.Add(epic);
            }
        }

        return context;
    }
}
