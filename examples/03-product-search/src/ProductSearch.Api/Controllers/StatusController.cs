using Microsoft.AspNetCore.Mvc;
using ProductSearch.Core.Services;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Api.Controllers;

/// <summary>Demo readiness and index counts.</summary>
[ApiController]
[Route("api/status")]
public sealed class StatusController : ControllerBase
{
    private readonly IStatusService _status;

    public StatusController(IStatusService status)
    {
        _status = status;
    }

    [HttpGet]
    public async Task<ActionResult<StatusDto>> GetAsync(CancellationToken cancellationToken)
        => Ok(await _status.GetStatusAsync(cancellationToken).ConfigureAwait(false));
}
