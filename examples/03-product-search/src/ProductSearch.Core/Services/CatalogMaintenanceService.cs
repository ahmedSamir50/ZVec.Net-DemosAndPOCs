using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProductSearch.Core.Data;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Models;
using ProductSearch.Core.Storage;

namespace ProductSearch.Core.Services;

public interface ICatalogMaintenanceService
{
    Task<int> DeleteBySeasonAsync(string season, CancellationToken ct = default);
    Task<int> ResetCatalogAsync(CancellationToken ct = default);
}

public sealed class CatalogMaintenanceService : ICatalogMaintenanceService
{
    private readonly IDbContextFactory<ProductDbContext> _dbFactory;
    private readonly DualCollectionHolder _collections;
    private readonly IIndexStampStore _stamp;
    private readonly ISigLipModelSelectionService _models;
    private readonly IngestProgressStatus _progress;
    private readonly ILogger<CatalogMaintenanceService> _logger;

    public CatalogMaintenanceService(
        IDbContextFactory<ProductDbContext> dbFactory,
        DualCollectionHolder collections,
        IIndexStampStore stamp,
        ISigLipModelSelectionService models,
        IngestProgressStatus progress,
        ILogger<CatalogMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _collections = collections;
        _stamp = stamp;
        _models = models;
        _progress = progress;
        _logger = logger;
    }

    public async Task<int> DeleteBySeasonAsync(string season, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(season);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Products
            .Where(p => p.Season == season)
            .Select(p => new { p.Id })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return 0;

        var ids = rows.Select(r => r.Id.ToString()).ToList();
        await _collections.DeleteByIdsAsync(ids, ct).ConfigureAwait(false);
        await db.Products.Where(p => p.Season == season).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Deleted {Count} products for season {Season}", rows.Count, season);
        return rows.Count;
    }

    public async Task<int> ResetCatalogAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var count = await db.Products.CountAsync(ct).ConfigureAwait(false);

        await db.Embeddings768.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Embeddings1152.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (count > 0)
            await db.Products.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var active = _models.ActiveDefinition;
        _collections.SwitchToModel(active);
        _collections.RecreateEmpty();
        _collections.EnsureIndexes();
        _stamp.Save(new IndexStamp(active.Id, active.EmbeddingDim, SigLipModelCatalog.EncodePipelineVersion, 0));
        _progress.SetIdle("Catalog and ZVec indexes cleared — start ingest to rebuild.");

        _logger.LogInformation("Reset catalog — deleted {Count} SQL rows and cleared ZVec indexes", count);
        return count;
    }
}
