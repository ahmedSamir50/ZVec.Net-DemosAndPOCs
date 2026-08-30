using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProductSearch.Core.Data;
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
    private readonly ILogger<CatalogMaintenanceService> _logger;

    public CatalogMaintenanceService(
        IDbContextFactory<ProductDbContext> dbFactory,
        DualCollectionHolder collections,
        ILogger<CatalogMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _collections = collections;
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
        if (count == 0)
            return 0;

        await db.Products.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Reset SQL catalog — deleted {Count} rows", count);
        return count;
    }
}
