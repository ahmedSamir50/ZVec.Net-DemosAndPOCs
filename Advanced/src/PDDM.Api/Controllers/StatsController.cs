using Microsoft.AspNetCore.Mvc;
using PDDM.Core.Abstractions;
using PDDM.Shared;
using PDDM.Shared.Dtos;

namespace PDDM.Api.Controllers;

/// <summary>Exposes hybrid-index and LM Studio health statistics.</summary>
[ApiController]
[Route("api/stats")]
public sealed class StatsController : ControllerBase
{
    private readonly IHybridIndex _hybridIndex;
    private readonly IEmbeddingService _embeddingService;

    /// <summary>Creates the stats controller.</summary>
    public StatsController(IHybridIndex hybridIndex, IEmbeddingService embeddingService)
    {
        _hybridIndex = hybridIndex;
        _embeddingService = embeddingService;
    }

    /// <summary>Returns document counts by tier and LM Studio reachability.</summary>
    [HttpGet]
    public async Task<ActionResult<StatsDto>> GetAsync(CancellationToken cancellationToken)
    {
        var tier3 = _hybridIndex.GetByTier((int)DocTier.Comment);
        return Ok(new StatsDto
        {
            TotalDocuments = _hybridIndex.TotalCount,
            Tier0Count = _hybridIndex.GetByTier((int)DocTier.EpicOrUmbrella).Count,
            Tier1Count = _hybridIndex.GetByTier((int)DocTier.Issue).Count,
            Tier2Count = _hybridIndex.GetByTier((int)DocTier.SubTask).Count,
            Tier3Count = tier3.Count,
            DecisionCommentCount = tier3.Count(c => c.ContainsDecision),
            LmStudioReachable = await _embeddingService.VerifyLmStudioAsync(cancellationToken).ConfigureAwait(false)
        });
    }
}
