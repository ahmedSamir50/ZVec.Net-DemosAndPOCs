using PDDM.Shared.Constants;
using PDDM.Shared.Dtos;
using PDDM.Shared.Sse;

namespace PDDM.UI.Services;

/// <summary>Consumes the chat SSE endpoint and raises token callbacks.</summary>
public sealed class ChatClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SseEventParser _parser;

    /// <summary>Creates the chat client.</summary>
    public ChatClientService(IHttpClientFactory httpClientFactory, SseEventParser parser)
    {
        _httpClientFactory = httpClientFactory;
        _parser = parser;
    }

    /// <summary>Streams a chat answer for the given question.</summary>
    public async Task StreamChatAsync(
        string question,
        Action<IntentEventDto>? onIntent,
        Action<ProgressEventDto>? onProgress,
        Action<string>? onToken,
        Action<string>? onError,
        Action? onDone,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.PddmApi);
        var url = $"{ApiRoutes.ChatStream.TrimStart('/')}?question={Uri.EscapeDataString(question)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? currentEvent = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal) || currentEvent is null)
                continue;

            var data = line["data:".Length..].Trim();
            var payload = _parser.Parse(currentEvent, data);
            switch (payload)
            {
                case IntentEventDto intent:
                    onIntent?.Invoke(intent);
                    break;
                case ProgressEventDto progress:
                    onProgress?.Invoke(progress);
                    break;
                case TokenEventDto token:
                    onToken?.Invoke(token.Token);
                    break;
                case ErrorEventDto error:
                    onError?.Invoke(error.Message);
                    break;
                case DoneEventDto:
                    onDone?.Invoke();
                    break;
            }
        }
    }
}
