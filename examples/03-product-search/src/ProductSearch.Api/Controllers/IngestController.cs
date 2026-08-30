using Microsoft.AspNetCore.Mvc;
using ProductSearch.Core.Services;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Api.Controllers;

/// <summary>Patch ingest, optimize, and reset operations.</summary>
[ApiController]
[Route("api/ingest")]
public sealed class IngestController : ControllerBase
{
    private readonly IIngestService _ingest;
    private readonly IngestProgressStatus _progress;
    private readonly ICatalogMaintenanceService _catalog;

    public IngestController(
        IIngestService ingest,
        IngestProgressStatus progress,
        ICatalogMaintenanceService catalog)
    {
        _ingest = ingest;
        _progress = progress;
        _catalog = catalog;
    }

    [HttpPost]
    public ActionResult<IngestProgressDto> RunAsync([FromBody] IngestRequestDto request)
    {
        var result = _ingest.TryStartPatch(request);
        if (!result.Started)
        {
            var snapshot = _progress.Snapshot();
            snapshot.ErrorMessage = result.Error;
            return BadRequest(snapshot);
        }

        return Ok(_progress.Snapshot());
    }

    [HttpGet]
    public ActionResult<IngestProgressDto> GetProgress()
        => Ok(_progress.Snapshot());

    [HttpPost("optimize")]
    public ActionResult Optimize()
    {
        var result = _ingest.TryOptimize();
        if (!result.Ok)
            return BadRequest(new { error = result.Error });
        return Ok(new { optimized = true });
    }

    [HttpPost("reset-indexes")]
    public ActionResult ResetIndexes()
    {
        var result = _ingest.TryResetIndexes();
        if (!result.Reset)
            return BadRequest(new { error = result.Error });
        return Ok(new { reset = true });
    }

    [HttpPost("reset-catalog")]
    public async Task<IActionResult> ResetCatalogAsync(CancellationToken cancellationToken)
    {
        var deleted = await _catalog.ResetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { reset = true, deleted });
    }
}
