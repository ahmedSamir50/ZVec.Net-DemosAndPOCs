using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Shared.Constants;

namespace ProductSearch.Api.Controllers;

/// <summary>Serves catalog JPEGs from the local cache folder.</summary>
[ApiController]
[Route("api/media")]
public sealed class MediaController : ControllerBase
{
    private readonly ProductSearchOptions _options;

    public MediaController(IOptions<ProductSearchOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet("{id:int}")]
    public IActionResult Get(int id)
    {
        var imagePath = Path.Combine(_options.CatalogImagesDirectory(), $"{id}.jpg");

        if (!System.IO.File.Exists(imagePath))
            return NotFound();

        return PhysicalFile(imagePath, "image/jpeg");
    }
}
