using Microsoft.AspNetCore.Mvc;
using ProductSearch.Core.Services;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly IProductSearchService _search;
    private readonly WowQueryProvider _wowQueries;
    private readonly IngestProgressStatus _progress;

    public SearchController(
        IProductSearchService search,
        WowQueryProvider wowQueries,
        IngestProgressStatus progress)
    {
        _search = search;
        _wowQueries = wowQueries;
        _progress = progress;
    }

    [HttpPost]
    public async Task<ActionResult<SearchResponseDto>> SearchAsync(
        [FromBody] SearchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (_progress.Snapshot().IsRunning)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new { error = "Ingest already running — try again after the patch." });
        }

        return Ok(await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("similar/{productId:guid}")]
    public async Task<ActionResult<SearchResponseDto>> SimilarAsync(
        Guid productId,
        [FromBody] SearchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (_progress.Snapshot().IsRunning)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new { error = "Ingest already running — try again after the patch." });
        }

        request.SimilarToProductId = productId;
        return Ok(await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("wow-queries")]
    public ActionResult<IReadOnlyList<WowQueryChipDto>> GetWowQueries()
        => Ok(_wowQueries.Load());
}
