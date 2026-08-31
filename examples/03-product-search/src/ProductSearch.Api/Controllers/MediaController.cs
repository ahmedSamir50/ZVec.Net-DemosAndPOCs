using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;

namespace ProductSearch.Api.Controllers;

/// <summary>Serves catalog JPEGs from the local cache folder.</summary>
[ApiController]
[Route("api/media")]
public sealed class MediaController : ControllerBase
{
    private readonly ProductSearchOptions _options;
    private readonly FashionDatasetDownloader _downloader;

    public MediaController(IOptions<ProductSearchOptions> options, FashionDatasetDownloader downloader)
    {
        _options = options.Value;
        _downloader = downloader;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var imagePath = Path.Combine(_options.CatalogImagesDirectory(), $"{id}.jpg");

        if (!System.IO.File.Exists(imagePath))
        {
            await _downloader.TryEnsureImageAsync(id.ToString(), ct).ConfigureAwait(false);
            if (!System.IO.File.Exists(imagePath))
                return NotFound();
        }

        return PhysicalFile(imagePath, "image/jpeg");
    }
}
