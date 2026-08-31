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

    public SearchController(
        IProductSearchService search,
        WowQueryProvider wowQueries)
    {
        _search = search;
        _wowQueries = wowQueries;
    }

    [HttpPost]
    public async Task<ActionResult<SearchResponseDto>> SearchAsync(
        [FromBody] SearchRequestDto request,
        CancellationToken cancellationToken)
        => Ok(await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false));

    [HttpPost("similar/{productId:guid}")]
    public async Task<ActionResult<SearchResponseDto>> SimilarAsync(
        Guid productId,
        [FromBody] SearchRequestDto request,
        CancellationToken cancellationToken)
    {
        request.SimilarToProductId = productId;
        return Ok(await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("wow-queries")]
    public ActionResult<IReadOnlyList<WowQueryChipDto>> GetWowQueries()
        => Ok(_wowQueries.Load());
}
