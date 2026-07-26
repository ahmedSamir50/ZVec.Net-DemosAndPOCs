using System.Text.Json;
using PDDM.Shared.Constants;
using PDDM.Shared.Dtos;

namespace PDDM.Shared.Sse;

/// <summary>Parses SSE data payloads for known PDDM event types.</summary>
public sealed class SseEventParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses a single SSE data JSON payload.</summary>
    public object? Parse(string eventType, string dataJson) => eventType switch
    {
        SseEventTypes.Intent => JsonSerializer.Deserialize<IntentEventDto>(dataJson, JsonOptions),
        SseEventTypes.Token => JsonSerializer.Deserialize<TokenEventDto>(dataJson, JsonOptions),
        SseEventTypes.Done => JsonSerializer.Deserialize<DoneEventDto>(dataJson, JsonOptions),
        SseEventTypes.Error => JsonSerializer.Deserialize<ErrorEventDto>(dataJson, JsonOptions),
        SseEventTypes.Progress => JsonSerializer.Deserialize<ProgressEventDto>(dataJson, JsonOptions),
        SseEventTypes.Prompt => JsonSerializer.Deserialize<PromptPackageEventDto>(dataJson, JsonOptions),
        _ => null
    };
}
