using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.Models.LmStudio;
using PDDM.Shared.Constants;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class ChatService : IChatService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PddmRuntimeSettings _runtimeSettings;

    /// <summary>Creates the chat service.</summary>
    public ChatService(IHttpClientFactory httpClientFactory, PddmRuntimeSettings runtimeSettings)
    {
        _httpClientFactory = httpClientFactory;
        _runtimeSettings = runtimeSettings;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lm = _runtimeSettings.Current.LmStudio;
        var client = _httpClientFactory.CreateClient(HttpClientNames.LmStudio);
        var request = new ChatCompletionRequest
        {
            Model = lm.ChatModel,
            Temperature = lm.ChatTemperature,
            MaxTokens = lm.ChatMaxTokens,
            Stream = true,
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt }
            ]
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                yield break;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                yield break;

            ChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(data, JsonOptions);
            }
            catch
            {
                continue;
            }

            var token = chunk?.Choices.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        await foreach (var token in StreamAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false))
            sb.Append(token);
        return sb.ToString();
    }
}
