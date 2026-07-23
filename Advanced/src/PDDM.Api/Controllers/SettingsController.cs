using Microsoft.AspNetCore.Mvc;
using PDDM.Core.Abstractions;
using PDDM.Shared.Dtos;

namespace PDDM.Api.Controllers;

/// <summary>LM Studio model settings and connectivity checks.</summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;

    /// <summary>Creates the settings controller.</summary>
    public SettingsController(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>Returns current LM Studio settings.</summary>
    [HttpGet]
    public ActionResult<LmStudioSettingsDto> Get()
        => Ok(_settingsService.GetLmStudioSettings());

    /// <summary>Updates LM Studio settings (embedding dimensions are locked).</summary>
    [HttpPut]
    public ActionResult<LmStudioSettingsDto> Update([FromBody] LmStudioSettingsDto dto)
    {
        try
        {
            _settingsService.UpdateLmStudioSettings(dto);
            return Ok(_settingsService.GetLmStudioSettings());
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorEventDto { Message = ex.Message });
        }
    }

    /// <summary>Verifies LM Studio is reachable.</summary>
    [HttpPost("verify")]
    public async Task<ActionResult<object>> VerifyAsync(CancellationToken cancellationToken)
    {
        var reachable = await _settingsService.VerifyLmStudioAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { reachable });
    }
}
