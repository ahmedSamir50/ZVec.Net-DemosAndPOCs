using PDDM.Shared;
using PDDM.Shared.Constants;

namespace PDDM.Shared.Dtos;

/// <summary>SSE envelope from API to UI.</summary>
public sealed class SseEventDto
{
    public string EventType { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>First SSE payload: detected intent.</summary>
public sealed class IntentEventDto
{
    public QueryIntent Intent { get; set; }
    public string? IssueKey { get; set; }
}

/// <summary>Token SSE payload.</summary>
public sealed class TokenEventDto
{
    public string Token { get; set; } = "";
}

/// <summary>Final SSE payload.</summary>
public sealed class DoneEventDto
{
    public bool Success { get; set; } = true;
}

/// <summary>Error SSE payload.</summary>
public sealed class ErrorEventDto
{
    public string Message { get; set; } = "";
}

/// <summary>Progress SSE payload for chat pipeline phases.</summary>
public sealed class ProgressEventDto
{
    public string Phase { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Ingestion progress for UI.</summary>
public sealed class IngestionProgressDto
{
    public int IssuesFetched { get; set; }
    public int ChunksCreated { get; set; }
    public int EmbeddingsGenerated { get; set; }
    public int ChunksInserted { get; set; }
    public string Status { get; set; } = "NotStarted";
    public string? ErrorMessage { get; set; }
}

/// <summary>Vector store / hybrid index stats.</summary>
public sealed class StatsDto
{
    public int TotalDocuments { get; set; }
    public int Tier0Count { get; set; }
    public int Tier1Count { get; set; }
    public int Tier2Count { get; set; }
    public int Tier3Count { get; set; }
    public int DecisionCommentCount { get; set; }
    public bool LmStudioReachable { get; set; }
}

/// <summary>LM Studio settings exposed to UI.</summary>
public sealed class LmStudioSettingsDto
{
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string EmbeddingModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";
    public string ChatModel { get; set; } = SharedPddmDefaults.DefaultChatModel;
    public int EmbeddingDimensions { get; set; } = SharedPddmDefaults.EmbeddingDimensions;
    public float ChatTemperature { get; set; } = 0.3f;
    public int ChatMaxTokens { get; set; } = -1;
    public int EmbeddingBatchSize { get; set; } = 50;
}

/// <summary>Chat request body (non-SSE fallback).</summary>
public sealed class ChatRequestDto
{
    public string Question { get; set; } = "";
}
