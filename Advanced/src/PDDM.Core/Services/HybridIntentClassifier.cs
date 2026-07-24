using System.Text.Json;
using System.Text.RegularExpressions;
using PDDM.Core.Abstractions;
using PDDM.Core.Constants;
using PDDM.Shared;

namespace PDDM.Core.Services;

/// <summary>
/// Hybrid intent: heuristic fast path for high-confidence hits; LLM JSON classify when ambiguous
/// (<see cref="QueryIntent.GeneralQuestion"/> from the heuristic).
/// </summary>
public sealed partial class HybridIntentClassifier : IIntentClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IntentClassifier _heuristic;
    private readonly IChatService _chatService;

    /// <summary>Creates the hybrid classifier.</summary>
    public HybridIntentClassifier(IntentClassifier heuristic, IChatService chatService)
    {
        _heuristic = heuristic;
        _chatService = chatService;
    }

    /// <inheritdoc />
    public QueryIntent Classify(string userInput) => _heuristic.Classify(userInput);

    /// <inheritdoc />
    public string? ExtractIssueKey(string userInput) => _heuristic.ExtractIssueKey(userInput);

    /// <inheritdoc />
    public async Task<QueryIntent> ClassifyAsync(string userInput, CancellationToken cancellationToken = default)
    {
        var heuristic = _heuristic.Classify(userInput);
        if (heuristic != QueryIntent.GeneralQuestion)
            return heuristic;

        if (string.IsNullOrWhiteSpace(userInput))
            return QueryIntent.GeneralQuestion;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PddmDefaults.IntentClassifyTimeoutMs);

            var raw = await _chatService.CompleteAsync(
                PddmDefaults.IntentClassifySystemPrompt,
                userInput,
                timeoutCts.Token,
                temperature: 0f,
                maxTokens: PddmDefaults.IntentClassifyMaxTokens).ConfigureAwait(false);

            return ParseIntent(raw) ?? QueryIntent.GeneralQuestion;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return QueryIntent.GeneralQuestion;
        }
        catch
        {
            return QueryIntent.GeneralQuestion;
        }
    }

    public static QueryIntent? ParseIntent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        var fence = JsonFenceRegex().Match(text);
        if (fence.Success)
            text = fence.Groups[1].Value.Trim();

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        text = text[start..(end + 1)];

        try
        {
            var dto = JsonSerializer.Deserialize<IntentJsonDto>(text, JsonOptions);
            if (dto?.Intent is null)
                return null;

            return Enum.TryParse<QueryIntent>(dto.Intent, ignoreCase: true, out var parsed)
                ? parsed
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class IntentJsonDto
    {
        public string? Intent { get; set; }
        public string? IssueKey { get; set; }
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonFenceRegex();
}
