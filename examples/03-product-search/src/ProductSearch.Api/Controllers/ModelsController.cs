using Microsoft.AspNetCore.Mvc;
using ProductSearch.Core.Services;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Api.Controllers;

/// <summary>SigLIP model selection.</summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly ISigLipModelSelectionService _models;

    public ModelsController(ISigLipModelSelectionService models)
    {
        _models = models;
    }

    [HttpGet]
    public ActionResult<ModelsResponseDto> List()
    {
        var active = _models.ActiveDefinition;
        return Ok(new ModelsResponseDto
        {
            ActiveModelId = active.Id,
            Models = _models.ListExpectations().Select(m => new ModelDefinitionDto
            {
                Id = m.Id,
                DisplayName = m.DisplayName,
                EmbeddingDim = m.EmbeddingDim,
                ImageSize = m.ImageSize
            }).ToList()
        });
    }

    [HttpPost("select")]
    public async Task<ActionResult<ModelSelectResultDto>> SelectAsync(
        [FromBody] ModelSelectRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId))
            return BadRequest(new ModelSelectResultDto { Ok = false, Error = "modelId is required" });

        var result = await _models.SelectAsync(request.ModelId.Trim(), cancellationToken).ConfigureAwait(false);
        var dto = new ModelSelectResultDto
        {
            Ok = result.Ok,
            Error = result.Error,
            ActiveModelId = result.ActiveModelId
        };
        return result.Ok ? Ok(dto) : BadRequest(dto);
    }
}
