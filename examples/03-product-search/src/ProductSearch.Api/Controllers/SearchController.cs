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
    private readonly ILogger<SearchController> _logger;

    public SearchController(
        IProductSearchService search,
        WowQueryProvider wowQueries,
        ILogger<SearchController> logger)
    {
        _search = search;
        _wowQueries = wowQueries;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<SearchResponseDto>> SearchAsync(
        [FromBody] SearchRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            throw;
        }
    }

    [HttpPost("similar/{productId:guid}")]
    public async Task<ActionResult<SearchResponseDto>> SimilarAsync(
        Guid productId,
        [FromBody] SearchRequestDto request,
        CancellationToken cancellationToken)
    {
        request.SimilarToProductId = productId;
        try
        {
            return Ok(await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Similar-to search failed for {ProductId}", productId);
            throw;
        }
    }

    [HttpGet("wow-queries")]
    public ActionResult<IReadOnlyList<WowQueryChipDto>> GetWowQueries()
        => Ok(_wowQueries.Load());
}
