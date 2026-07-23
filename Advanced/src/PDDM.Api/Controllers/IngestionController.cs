using Microsoft.AspNetCore.Mvc;
using PDDM.Core.Abstractions;
using PDDM.Shared.Dtos;

namespace PDDM.Api.Controllers;

/// <summary>Triggers and reports Jira → ZVec ingestion.</summary>
[ApiController]
[Route("api/ingestion")]
public sealed class IngestionController : ControllerBase
{
    private readonly IIngestionOrchestrator _orchestrator;

    /// <summary>Creates the ingestion controller.</summary>
    public IngestionController(IIngestionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>Runs a full ingestion pipeline.</summary>
    /// <remarks>
    /// Does not use <see cref="HttpContext.RequestAborted"/> — Aspire HttpClient resilience
    /// (and UI disconnects) would otherwise cancel mid-embed after ~30s. Progress is polled via GET.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<IngestionProgressDto>> RunAsync()
    {
        var progress = await _orchestrator.RunAsync(CancellationToken.None).ConfigureAwait(false);
        return Ok(ToDto(progress));
    }

    /// <summary>Returns the latest ingestion progress snapshot.</summary>
    [HttpGet]
    public ActionResult<IngestionProgressDto> GetProgress()
        => Ok(ToDto(_orchestrator.GetProgress()));

    private static IngestionProgressDto ToDto(Core.Models.IngestionProgress progress) => new()
    {
        IssuesFetched = progress.IssuesFetched,
        ChunksCreated = progress.ChunksCreated,
        EmbeddingsGenerated = progress.EmbeddingsGenerated,
        ChunksInserted = progress.ChunksInserted,
        Status = progress.Status,
        ErrorMessage = progress.ErrorMessage
    };
}
