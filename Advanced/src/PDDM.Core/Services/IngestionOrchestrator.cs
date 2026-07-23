using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.Models;
using PDDM.Core.Models.JiraApi;
using PDDM.Shared.Constants;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class IngestionOrchestrator : IIngestionOrchestrator
{
    private readonly IJiraFetcher _jiraFetcher;
    private readonly IChunkingService _chunkingService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IHybridIndex _hybridIndex;
    private readonly PddmRuntimeSettings _runtimeSettings;
    private readonly object _progressGate = new();
    private IngestionProgress _progress = new();

    /// <summary>Creates the ingestion orchestrator.</summary>
    public IngestionOrchestrator(
        IJiraFetcher jiraFetcher,
        IChunkingService chunkingService,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IHybridIndex hybridIndex,
        PddmRuntimeSettings runtimeSettings)
    {
        _jiraFetcher = jiraFetcher;
        _chunkingService = chunkingService;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _hybridIndex = hybridIndex;
        _runtimeSettings = runtimeSettings;
    }

    /// <inheritdoc />
    public IngestionProgress GetProgress()
    {
        lock (_progressGate)
            return Clone(_progress);
    }

    /// <inheritdoc />
    public async Task<IngestionProgress> RunAsync(CancellationToken cancellationToken = default)
    {
        SetProgress(p =>
        {
            p.Status = IngestionStatus.Running;
            p.StartedAt = DateTime.UtcNow;
            p.ErrorMessage = null;
            p.IssuesFetched = 0;
            p.ChunksCreated = 0;
            p.EmbeddingsGenerated = 0;
            p.ChunksInserted = 0;
        });

        var warnings = new List<string>();

        try
        {
            var ingestion = _runtimeSettings.Current.Ingestion;
            var jira = _runtimeSettings.Current.Jira;
            var issues = new List<JiraIssue>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.Epic, ingestion.MaxEpics, warnings, cancellationToken).ConfigureAwait(false);
            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.Story, ingestion.MaxStories, warnings, cancellationToken).ConfigureAwait(false);
            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.Umbrella, ingestion.MaxUmbrellas, warnings, cancellationToken).ConfigureAwait(false);
            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.Bug, ingestion.MaxBugs, warnings, cancellationToken).ConfigureAwait(false);
            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.Improvement, ingestion.MaxImprovements, warnings, cancellationToken).ConfigureAwait(false);
            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.Task, ingestion.MaxTasks, warnings, cancellationToken).ConfigureAwait(false);
            await FetchTypeSafeAsync(issues, seenKeys, JiraIssueTypeNames.SubTask, ingestion.MaxSubTasks, warnings, cancellationToken).ConfigureAwait(false);

            var ansiJql = $"project={jira.ProjectKey} AND text ~ \"ANSI\" ORDER BY updated DESC";
            try
            {
                var ansiHits = await _jiraFetcher.FetchByJqlAsync(ansiJql, ingestion.MaxAnsiJqlHits, cancellationToken).ConfigureAwait(false);
                MergeIssues(issues, seenKeys, ansiHits);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"ANSI JQL: {ex.Message}");
            }

            await SeedGoldenKeysAsync(issues, seenKeys, warnings, cancellationToken).ConfigureAwait(false);

            SetProgress(p => p.IssuesFetched = issues.Count);

            if (issues.Count == 0)
                throw new InvalidOperationException("No issues fetched. Check Jira connectivity and ingestion limits.");

            _vectorStore.RecreateCollection();
            _hybridIndex.Clear();

            var chunks = _chunkingService.CreateChunks(issues).ToList();
            SetProgress(p => p.ChunksCreated = chunks.Count);

            var batchSize = _runtimeSettings.Current.LmStudio.EmbeddingBatchSize;
            for (var i = 0; i < chunks.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                var texts = batch.Select(_chunkingService.ComposeEmbeddingText).ToList();
                var vectors = await _embeddingService.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
                for (var j = 0; j < batch.Count; j++)
                    batch[j].Embedding = vectors[j];

                _vectorStore.InsertBatch(batch);
                SetProgress(p =>
                {
                    p.EmbeddingsGenerated += batch.Count;
                    p.ChunksInserted += batch.Count;
                });
            }

            _hybridIndex.Clear();
            _hybridIndex.AddRange(chunks);
            await _vectorStore.SaveChunkIdsAsync(chunks.Select(c => c.Id), cancellationToken).ConfigureAwait(false);

            SetProgress(p =>
            {
                p.Status = IngestionStatus.Completed;
                p.CompletedAt = DateTime.UtcNow;
                if (warnings.Count > 0)
                    p.ErrorMessage = string.Join(" | ", warnings);
            });
        }
        catch (OperationCanceledException)
        {
            SetProgress(p =>
            {
                p.Status = IngestionStatus.Cancelled;
                p.ErrorMessage = "Ingestion was cancelled.";
                p.CompletedAt = DateTime.UtcNow;
            });
            return GetProgress();
        }
        catch (Exception ex)
        {
            SetProgress(p =>
            {
                p.Status = IngestionStatus.Failed;
                p.ErrorMessage = warnings.Count == 0
                    ? ex.Message
                    : $"{ex.Message} | {string.Join(" | ", warnings)}";
                p.CompletedAt = DateTime.UtcNow;
            });
            throw;
        }

        return GetProgress();
    }

    private async Task FetchTypeSafeAsync(
        List<JiraIssue> issues,
        HashSet<string> seenKeys,
        string issueType,
        int maxTotal,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var fetched = await _jiraFetcher.FetchByTypeAsync(issueType, maxTotal, cancellationToken).ConfigureAwait(false);
            MergeIssues(issues, seenKeys, fetched);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"{issueType}: {ex.Message}");
        }
    }

    private async Task SeedGoldenKeysAsync(
        List<JiraIssue> issues,
        HashSet<string> seenKeys,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var key in GoldenDemoSeedKeys.All)
        {
            try
            {
                if (seenKeys.Contains(key))
                    continue;

                var issue = await _jiraFetcher.FetchSingleAsync(key, cancellationToken).ConfigureAwait(false);
                if (issue is null)
                {
                    warnings.Add($"Seed {key}: not found");
                    continue;
                }

                MergeIssues(issues, seenKeys, [issue]);

                var epicKey = issue.Fields.EpicLink;
                if (!string.IsNullOrWhiteSpace(epicKey) && !seenKeys.Contains(epicKey))
                {
                    var epic = await _jiraFetcher.FetchSingleAsync(epicKey, cancellationToken).ConfigureAwait(false);
                    if (epic is not null)
                        MergeIssues(issues, seenKeys, [epic]);

                    try
                    {
                        var children = await _jiraFetcher.FetchEpicChildrenAsync(epicKey, cancellationToken).ConfigureAwait(false);
                        MergeIssues(issues, seenKeys, children);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Epic children {epicKey}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"Seed {key}: {ex.Message}");
            }
        }
    }

    private static void MergeIssues(List<JiraIssue> issues, HashSet<string> seenKeys, IEnumerable<JiraIssue> incoming)
    {
        foreach (var issue in incoming)
        {
            if (string.IsNullOrWhiteSpace(issue.Key) || !seenKeys.Add(issue.Key))
                continue;
            issues.Add(issue);
        }
    }

    private void SetProgress(Action<IngestionProgress> mutate)
    {
        lock (_progressGate)
            mutate(_progress);
    }

    private static IngestionProgress Clone(IngestionProgress p) => new()
    {
        IssuesFetched = p.IssuesFetched,
        ChunksCreated = p.ChunksCreated,
        EmbeddingsGenerated = p.EmbeddingsGenerated,
        ChunksInserted = p.ChunksInserted,
        Status = p.Status,
        ErrorMessage = p.ErrorMessage,
        StartedAt = p.StartedAt,
        CompletedAt = p.CompletedAt
    };
}
