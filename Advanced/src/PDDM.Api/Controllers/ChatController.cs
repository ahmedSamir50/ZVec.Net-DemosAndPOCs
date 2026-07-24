using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PDDM.Core.Abstractions;
using PDDM.Core.Constants;
using PDDM.Shared.Constants;
using PDDM.Shared.Dtos;

namespace PDDM.Api.Controllers;

/// <summary>SSE chat streaming over project-docs navigation.</summary>
[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
    private readonly IIntentClassifier _intentClassifier;
    private readonly INavigationEngine _navigationEngine;
    private readonly IChatService _chatService;

    /// <summary>Creates the chat controller.</summary>
    public ChatController(
        IIntentClassifier intentClassifier,
        INavigationEngine navigationEngine,
        IChatService chatService)
    {
        _intentClassifier = intentClassifier;
        _navigationEngine = navigationEngine;
        _chatService = chatService;
    }

    /// <summary>Streams intent, progress, tokens, and completion events for a question.</summary>
    [HttpGet("stream")]
    public async Task StreamAsync([FromQuery] string question, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        try
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                await WriteEventAsync(SseEventTypes.Error, new ErrorEventDto { Message = "question is required" }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await WriteEventAsync(SseEventTypes.Progress, new ProgressEventDto
            {
                Phase = ChatProgressPhases.Classifying,
                Message = "Classifying intent…"
            }, cancellationToken).ConfigureAwait(false);

            var intent = await _intentClassifier.ClassifyAsync(question, cancellationToken).ConfigureAwait(false);
            await WriteEventAsync(SseEventTypes.Intent, new IntentEventDto
            {
                Intent = intent,
                IssueKey = _intentClassifier.ExtractIssueKey(question)
            }, cancellationToken).ConfigureAwait(false);

            await WriteEventAsync(SseEventTypes.Progress, new ProgressEventDto
            {
                Phase = ChatProgressPhases.Retrieving,
                Message = "Retrieving project docs…"
            }, cancellationToken).ConfigureAwait(false);

            var nav = await _navigationEngine.NavigateAsync(question, intent, cancellationToken).ConfigureAwait(false);
            var systemPrompt = PddmDefaults.BuildSystemPrompt(nav.Intent);
            var userPrompt = PddmDefaults.BuildUserPrompt(nav.AssembledContext, question, nav.Intent);

            await WriteEventAsync(SseEventTypes.Progress, new ProgressEventDto
            {
                Phase = ChatProgressPhases.Generating,
                Message = "Generating navigator answer…"
            }, cancellationToken).ConfigureAwait(false);

            await foreach (var token in _chatService.StreamAsync(systemPrompt, userPrompt, cancellationToken)
                               .ConfigureAwait(false))
            {
                await WriteEventAsync(SseEventTypes.Token, new TokenEventDto { Token = token }, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteEventAsync(SseEventTypes.Done, new DoneEventDto { Success = true }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteEventAsync(SseEventTypes.Error, new ErrorEventDto { Message = ex.Message }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteEventAsync(string eventType, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await Response.WriteAsync($"event: {eventType}\n", cancellationToken).ConfigureAwait(false);
        await Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
