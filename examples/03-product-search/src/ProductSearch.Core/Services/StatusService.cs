using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Storage;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Core.Services;

public interface IStatusService
{
    Task<StatusDto> GetStatusAsync(CancellationToken ct = default);
}

public sealed class StatusService : IStatusService
{
    private readonly ISigLipEncoder _encoder;
    private readonly ISigLipModelSelectionService _models;
    private readonly IIndexStampStore _stamp;
    private readonly DualCollectionHolder _collections;
    private readonly IDbContextFactory<ProductDbContext> _dbFactory;
    private readonly FashionCatalogReader _catalogReader;
    private readonly ModelBootstrapStatus _bootstrap;
    private readonly ProductSearchOptions _options;

    public StatusService(
        ISigLipEncoder encoder,
        ISigLipModelSelectionService models,
        IIndexStampStore stamp,
        DualCollectionHolder collections,
        IDbContextFactory<ProductDbContext> dbFactory,
        FashionCatalogReader catalogReader,
        ModelBootstrapStatus bootstrap,
        IOptions<ProductSearchOptions> options)
    {
        _encoder = encoder;
        _models = models;
        _stamp = stamp;
        _collections = collections;
        _dbFactory = dbFactory;
        _catalogReader = catalogReader;
        _bootstrap = bootstrap;
        _options = options.Value;
    }

    public async Task<StatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var active = _models.ActiveDefinition;
        var stamp = _stamp.Load();
        var stampMatch = !_stamp.IsMismatch(active, stamp);
        var (textCount, imageCount) = _collections.DocCounts;

        var sqlCount = 0;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            sqlCount = await db.Products.CountAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Postgres may be unavailable during bootstrap.
        }

        var catalogTotal = 0;
        try
        {
            var catalog = await _catalogReader.ReadAllAsync(ct).ConfigureAwait(false);
            catalogTotal = catalog.Count;
        }
        catch
        {
            // data.csv may not be extracted from the in-repo pack yet.
        }

        string? indexWarning = null;
        if (stamp.IngestOffset <= 0)
            indexWarning = "Indexes are empty. Run ingest before searching.";
        else if (sqlCount != textCount || sqlCount != imageCount)
            indexWarning = $"Count mismatch — SQL={sqlCount}, ZVec text={textCount}, ZVec image={imageCount}.";

        var demoReady = _encoder.IsReady && stampMatch && stamp.IngestOffset > 0
                        && sqlCount > 0 && textCount > 0 && imageCount > 0
                        && sqlCount == textCount && textCount == imageCount;

        var boot = _bootstrap.Snapshot();

        return new StatusDto
        {
            PostgresCount = sqlCount,
            ZVecTextCount = (int)textCount,
            ZVecImageCount = (int)imageCount,
            ActiveModelId = active.Id,
            EmbeddingDim = active.EmbeddingDim,
            StampMatch = stampMatch,
            DemoReady = demoReady,
            IngestOffset = stamp.IngestOffset,
            CatalogTotal = catalogTotal,
            ModelBootstrapComplete = _encoder.IsReady,
            ModelBootstrap = ToDto(boot),
            StampWarning = stampMatch ? null : _stamp.MismatchMessage(active, stamp),
            IndexWarning = indexWarning,
            Postgres = ParsePostgresConnection(_options.PostgresConnectionString)
        };
    }

    private static PostgresConnectionDto ParsePostgresConnection(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return new PostgresConnectionDto
            {
                Host = builder.Host ?? "localhost",
                Port = builder.Port > 0 ? builder.Port : 5432,
                Database = builder.Database ?? "productsearch",
                Username = builder.Username ?? "postgres",
                Password = builder.Password ?? ""
            };
        }
        catch
        {
            return new PostgresConnectionDto();
        }
    }

    private static ModelBootstrapSnapshotDto ToDto(ModelBootstrapSnapshot snap)
        => new()
        {
            State = snap.State,
            ModelsDir = snap.ModelsDir,
            Message = snap.Message,
            Error = snap.Error,
            ErrorDetail = snap.ErrorDetail,
            OverallPercent = snap.OverallPercent,
            Files = snap.Files.Select(f => new ModelFileProgressDto
            {
                Name = f.Name,
                Status = f.Status.ToString(),
                BytesReceived = f.BytesReceived,
                BytesTotal = f.BytesTotal,
                Percent = f.Percent,
                OnDisk = f.OnDisk,
                FullPath = f.FullPath
            }).ToList()
        };
}
