using PDDM.Core.Constants;

namespace PDDM.Core.Configuration;

/// <summary>Root PDDM settings bound from configuration.</summary>
public sealed class PddmSettings
{
    public LmStudioSettings LmStudio { get; set; } = new();
    public JiraSettings Jira { get; set; } = new();
    public ZVecSettings ZVec { get; set; } = new();
    public IngestionSettings Ingestion { get; set; } = new();
}

/// <summary>LM Studio connection and model settings.</summary>
public sealed class LmStudioSettings
{
    public string BaseUrl { get; set; } = PddmDefaults.DefaultLmStudioBaseUrl;
    public string EmbeddingModel { get; set; } = PddmDefaults.DefaultEmbeddingModel;
    public string ChatModel { get; set; } = PddmDefaults.DefaultChatModel;
    public int EmbeddingDimensions { get; set; } = PddmDefaults.EmbeddingDimensions;
    public float ChatTemperature { get; set; } = 0.3f;
    public int ChatMaxTokens { get; set; } = -1;
    public int EmbeddingBatchSize { get; set; } = 50;
}

/// <summary>Apache Jira REST settings.</summary>
public sealed class JiraSettings
{
    public string BaseUrl { get; set; } = PddmDefaults.DefaultJiraBaseUrl;
    public string ProjectKey { get; set; } = PddmDefaults.DefaultJiraProjectKey;
    public int MaxResultsPerRequest { get; set; } = 100;
    public int RequestDelayMs { get; set; } = 1000;
}

/// <summary>ZVec.NET collection path and mmap options.</summary>
public sealed class ZVecSettings
{
    public string CollectionPath { get; set; } = PddmDefaults.DefaultCollectionPath;
    public string LogLevel { get; set; } = "Warn";
    public int QueryThreads { get; set; } = -1;
    public int MemoryLimitMb { get; set; } = 512;
    public bool EnableMmap { get; set; } = true;
}

/// <summary>Ingestion volume limits.</summary>
public sealed class IngestionSettings
{
    public int MaxEpics { get; set; } = 74;
    public int MaxStories { get; set; } = 138;
    public int MaxUmbrellas { get; set; } = 100;
    public int MaxBugs { get; set; } = 200;
    public int MaxImprovements { get; set; } = 200;
    public int MaxTasks { get; set; } = 50;
    public int MaxSubTasks { get; set; } = 200;
    public int MaxAnsiJqlHits { get; set; } = 40;
    public int MaxCommentsPerIssue { get; set; } = 10;
    public int MinCommentsForIssue { get; set; } = 2;
}

/// <summary>
/// Mutable runtime settings singleton. Updated by SettingsService; services inject this, not IOptionsMonitor.
/// </summary>
public sealed class PddmRuntimeSettings
{
    private readonly object _gate = new();
    private PddmSettings _current;

    /// <summary>Creates runtime settings from initial configuration snapshot.</summary>
    public PddmRuntimeSettings(PddmSettings initial)
    {
        _current = Clone(initial);
    }

    /// <summary>Thread-safe snapshot of current settings.</summary>
    public PddmSettings Current
    {
        get
        {
            lock (_gate)
                return Clone(_current);
        }
    }

    /// <summary>Replaces LM Studio settings. Rejects embedding dimension changes.</summary>
    public void UpdateLmStudio(LmStudioSettings lmStudio)
    {
        ArgumentNullException.ThrowIfNull(lmStudio);
        lock (_gate)
        {
            if (lmStudio.EmbeddingDimensions != PddmDefaults.EmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding dimension is locked to {PddmDefaults.EmbeddingDimensions}. Destroy the collection and re-ingest to change dimensions.");
            }

            _current.LmStudio = CloneLm(lmStudio);
        }
    }

    /// <summary>Replaces full settings snapshot (used at bootstrap only for non-LM sections).</summary>
    public void Replace(PddmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
            _current = Clone(settings);
    }

    private static PddmSettings Clone(PddmSettings s) => new()
    {
        LmStudio = CloneLm(s.LmStudio),
        Jira = new JiraSettings
        {
            BaseUrl = s.Jira.BaseUrl,
            ProjectKey = s.Jira.ProjectKey,
            MaxResultsPerRequest = s.Jira.MaxResultsPerRequest,
            RequestDelayMs = s.Jira.RequestDelayMs
        },
        ZVec = new ZVecSettings
        {
            CollectionPath = s.ZVec.CollectionPath,
            LogLevel = s.ZVec.LogLevel,
            QueryThreads = s.ZVec.QueryThreads,
            MemoryLimitMb = s.ZVec.MemoryLimitMb,
            EnableMmap = s.ZVec.EnableMmap
        },
        Ingestion = new IngestionSettings
        {
            MaxEpics = s.Ingestion.MaxEpics,
            MaxStories = s.Ingestion.MaxStories,
            MaxUmbrellas = s.Ingestion.MaxUmbrellas,
            MaxBugs = s.Ingestion.MaxBugs,
            MaxImprovements = s.Ingestion.MaxImprovements,
            MaxTasks = s.Ingestion.MaxTasks,
            MaxSubTasks = s.Ingestion.MaxSubTasks,
            MaxAnsiJqlHits = s.Ingestion.MaxAnsiJqlHits,
            MaxCommentsPerIssue = s.Ingestion.MaxCommentsPerIssue,
            MinCommentsForIssue = s.Ingestion.MinCommentsForIssue
        }
    };

    private static LmStudioSettings CloneLm(LmStudioSettings s) => new()
    {
        BaseUrl = s.BaseUrl,
        EmbeddingModel = s.EmbeddingModel,
        ChatModel = s.ChatModel,
        EmbeddingDimensions = s.EmbeddingDimensions,
        ChatTemperature = s.ChatTemperature,
        ChatMaxTokens = s.ChatMaxTokens,
        EmbeddingBatchSize = s.EmbeddingBatchSize
    };
}
