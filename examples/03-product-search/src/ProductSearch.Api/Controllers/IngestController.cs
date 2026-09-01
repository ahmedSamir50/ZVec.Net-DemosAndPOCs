using Microsoft.AspNetCore.Mvc;
using ProductSearch.Core.Data;
using ProductSearch.Core.Services;
using ProductSearch.Core.Storage;
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
    private readonly IIndexStampStore _stamp;
    private readonly FashionCatalogReader _catalogReader;
    private readonly ILogger<IngestController> _logger;

    public IngestController(
        IIngestService ingest,
        IngestProgressStatus progress,
        ICatalogMaintenanceService catalog,
        IIndexStampStore stamp,
        FashionCatalogReader catalogReader,
        ILogger<IngestController> logger)
    {
        _ingest = ingest;
        _progress = progress;
        _catalog = catalog;
        _stamp = stamp;
        _catalogReader = catalogReader;
        _logger = logger;
    }

    [HttpPost]
    public ActionResult<IngestProgressDto> RunAsync([FromBody] IngestRequestDto request)
    {
        var result = _ingest.TryStartPatch(request);
        if (!result.Started)
        {
            _logger.LogWarning("Ingest refused: {Error}", result.Error);
            var snapshot = _progress.Snapshot();
            snapshot.ErrorMessage = result.Error;
            return BadRequest(snapshot);
        }

        return Ok(_progress.Snapshot());
    }

    [HttpGet]
    public async Task<ActionResult<IngestProgressDto>> GetProgressAsync(CancellationToken cancellationToken)
    {
        var snapshot = _progress.Snapshot();
        if (snapshot.IsRunning)
            return Ok(_progress.SnapshotHydrated(_stamp.Load(), snapshot.CatalogTotal));

        var catalogTotal = 0;
        try
        {
            catalogTotal = await _catalogReader.GetCatalogTotalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Catalog total unavailable during ingest progress poll");
        }

        return Ok(_progress.SnapshotHydrated(_stamp.Load(), catalogTotal));
    }

    [HttpPost("optimize")]
    public ActionResult Optimize()
    {
        var result = _ingest.TryOptimize();
        if (!result.Ok)
        {
            _logger.LogWarning("Optimize refused: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }
        return Ok(new { optimized = true });
    }

    [HttpPost("reset-indexes")]
    public ActionResult ResetIndexes()
    {
        var result = _ingest.TryResetIndexes();
        if (!result.Reset)
        {
            _logger.LogWarning("Reset indexes refused: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }
        return Ok(new { reset = true });
    }

    [HttpPost("reset-catalog")]
    public async Task<IActionResult> ResetCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _catalog.ResetCatalogAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { reset = true, deleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset catalog failed");
            throw;
        }
    }
}
