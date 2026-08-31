using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Models;
using ProductSearch.Core.Storage;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Core.Services;

public sealed record IngestStartResult(bool Started, int PatchSize, string? Error);
public sealed record IngestResetResult(bool Reset, string? Error);
public sealed record IngestOptimizeResult(bool Ok, string? Error);

public interface IIngestService
{
    IngestStartResult TryStartPatch(IngestRequestDto request);
    IngestResetResult TryResetIndexes();
    IngestOptimizeResult TryOptimize();
}

public sealed class IngestService : IIngestService
{
    private readonly DualCollectionHolder _collections;
    private readonly ISigLipEncoder _encoder;
    private readonly ISigLipModelSelectionService _models;
    private readonly IIndexStampStore _stamp;
    private readonly FashionCatalogReader _catalogReader;
    private readonly FashionDatasetDownloader _downloader;
    private readonly IDbContextFactory<ProductDbContext> _dbFactory;
    private readonly ProductSearchOptions _options;
    private readonly IngestProgressStatus _progress;
    private readonly ILogger<IngestService> _logger;
    private readonly object _startGate = new();
    private int _running;

    public IngestService(
        DualCollectionHolder collections,
        ISigLipEncoder encoder,
        ISigLipModelSelectionService models,
        IIndexStampStore stamp,
        FashionCatalogReader catalogReader,
        FashionDatasetDownloader downloader,
        IDbContextFactory<ProductDbContext> dbFactory,
        IOptions<ProductSearchOptions> options,
        IngestProgressStatus progress,
        ILogger<IngestService> logger)
    {
        _collections = collections;
        _encoder = encoder;
        _models = models;
        _stamp = stamp;
        _catalogReader = catalogReader;
        _downloader = downloader;
        _dbFactory = dbFactory;
        _options = options.Value;
        _progress = progress;
        _logger = logger;
    }

    public IngestStartResult TryStartPatch(IngestRequestDto request)
    {
        var patchSize = request.PatchSize > 0 ? request.PatchSize : _options.DefaultPatchSize;
        if (!_options.AllowedPatchSizes.Contains(patchSize))
            patchSize = _options.DefaultPatchSize;

        if (!_encoder.IsReady)
            return new IngestStartResult(false, patchSize, _encoder.NotReadyReason ?? "SigLIP encoder is not ready.");

        var active = _models.ActiveDefinition;
        if (_stamp.IsMismatch(active))
            return new IngestStartResult(false, patchSize, _stamp.MismatchMessage(active) + " Reset indexes first.");

        lock (_startGate)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return new IngestStartResult(false, patchSize, "Ingest already running.");

            var patchIndex = (_stamp.Load().IngestOffset / Math.Max(1, patchSize)) + 1;
            _progress.ResetForPatch(patchSize, patchIndex);
            _logger.LogInformation("Starting ingest patch {PatchIndex} size {PatchSize}", patchIndex, patchSize);
            _ = Task.Run(() => RunPatchAsync(patchSize, request.OptimizeAfterPatch));
            return new IngestStartResult(true, patchSize, null);
        }
    }

    public IngestResetResult TryResetIndexes()
    {
        lock (_startGate)
        {
            if (Volatile.Read(ref _running) != 0)
                return new IngestResetResult(false, "Ingest already running.");

            try
            {
                var active = _models.ActiveDefinition;
                _collections.SwitchToModel(active);
                _collections.RecreateEmpty();
                _collections.EnsureIndexes();
                _stamp.Save(new IndexStamp(active.Id, active.EmbeddingDim, SigLipModelCatalog.EncodePipelineVersion, 0));
                _progress.SetIdle($"Indexes reset for {active.DisplayName}. Start ingest to re-embed.");
                return new IngestResetResult(true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Index reset failed");
                return new IngestResetResult(false, ex.Message);
            }
        }
    }

    public IngestOptimizeResult TryOptimize()
    {
        lock (_startGate)
        {
            if (Volatile.Read(ref _running) != 0)
                return new IngestOptimizeResult(false, "Ingest already running.");

            try
            {
                _collections.OptimizeBoth();
                _progress.SetIdle($"Optimized HNSW indexes for {_models.ActiveDefinition.DisplayName}.");
                return new IngestOptimizeResult(true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Optimize failed");
                return new IngestOptimizeResult(false, ex.Message);
            }
        }
    }

    private async Task RunPatchAsync(int patchSize, bool optimizeAfterPatch)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var writtenIds = new List<string>();
        try
        {
            var active = _models.ActiveDefinition;
            _logger.LogInformation("Ingest patch: ensuring catalog CSV from pack…");
            await _downloader.EnsureStylesCsvAsync().ConfigureAwait(false);
            var catalog = await _catalogReader.ReadAllAsync().ConfigureAwait(false);
            _logger.LogInformation("Ingest patch: catalog loaded ({Count} rows)", catalog.Count);
            var stamp = _stamp.Load();
            var offset = Math.Clamp(stamp.IngestOffset, 0, catalog.Count);

            await using (var dbCheck = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false))
            {
                var sqlCount = await dbCheck.Products.CountAsync().ConfigureAwait(false);
                if (sqlCount == 0 && stamp.IngestOffset > 0)
                {
                    _logger.LogWarning(
                        "SQL catalog empty but ingest offset is {Offset} — rewinding stamp and clearing orphan ZVec docs",
                        stamp.IngestOffset);
                    _collections.SwitchToModel(active);
                    _collections.RecreateEmpty();
                    _collections.EnsureIndexes();
                    offset = 0;
                    stamp = new IndexStamp(active.Id, active.EmbeddingDim, SigLipModelCatalog.EncodePipelineVersion, 0);
                    _stamp.Save(stamp);
                }
            }

            _collections.EnsureIndexes();

            if (_stamp.IsMismatch(active, stamp) && stamp.IngestOffset > 0)
                throw new InvalidOperationException(_stamp.MismatchMessage(active, stamp));

            var remaining = Math.Max(0, catalog.Count - offset);
            var target = Math.Min(patchSize, remaining);
            if (target == 0)
            {
                _progress.SetCompleted($"Caught up — offset {offset}/{catalog.Count}.", offset, catalog.Count, 0, 0, 0, sw.ElapsedMilliseconds);
                _logger.LogInformation("Ingest patch: nothing left to ingest at offset {Offset}/{Total}", offset, catalog.Count);
                return;
            }

            _logger.LogInformation(
                "Ingest patch: encoding {Target} products starting at catalog offset {Offset}/{Total}",
                target, offset, catalog.Count);

            var textBatch = new List<(string Id, string ConcatenatedText, string Gender, string BaseColour, string Season, string Usage, string MasterCategory, float[] Embedding)>();
            var imageBatch = new List<(string Id, float[] Embedding)>();
            var entities = new List<ProductEntity>();
            writtenIds.Clear();

            for (var i = 0; i < target; i++)
            {
                var product = catalog[offset + i];
                var id = ProductIdGenerator.StringFromCatalogId(product.CatalogId);

                _progress.SetEncoding(
                    $"Encoding {i + 1}/{target} (catalog offset {offset + i + 1}/{catalog.Count})…",
                    offset + i,
                    catalog.Count,
                    i + 1);

                var imagePath = await _downloader.TryEnsureImageAsync(product.CatalogId).ConfigureAwait(false);
                if (imagePath is null)
                {
                    _logger.LogWarning("Skipping catalog id {CatalogId} — image not found in pack.", product.CatalogId);
                    continue;
                }

                var textEmbedding = _encoder.EncodeText(product.ConcatenatedText);
                var imageEmbedding = _encoder.EncodeImage(imagePath);

                textBatch.Add((id, product.ConcatenatedText, product.Gender, product.BaseColour, product.Season, product.Usage, product.MasterCategory, textEmbedding));
                imageBatch.Add((id, imageEmbedding));
                entities.Add(new ProductEntity
                {
                    Id = Guid.Parse(id),
                    CatalogId = product.CatalogId,
                    Gender = product.Gender,
                    MasterCategory = product.MasterCategory,
                    SubCategory = product.SubCategory,
                    ArticleType = product.ArticleType,
                    BaseColour = product.BaseColour,
                    Season = product.Season,
                    Year = product.Year,
                    Usage = product.Usage,
                    ProductDisplayName = product.ProductDisplayName,
                    ConcatenatedText = product.ConcatenatedText,
                    ImageRelPath = product.ImageRelPath,
                    TextEmbedding = new Vector(textEmbedding),
                    ImageEmbedding = new Vector(imageEmbedding),
                    UpdatedUtc = DateTimeOffset.UtcNow
                });
                writtenIds.Add(id);
            }

            _progress.SetUpsertingZVec($"Upserting {writtenIds.Count} docs to ZVec…", writtenIds.Count);
            _logger.LogInformation("Ingest patch: upserting {Count} vectors to ZVec", writtenIds.Count);
            await _collections.UpsertTextBatchAsync(textBatch).ConfigureAwait(false);
            await _collections.UpsertImageBatchAsync(imageBatch).ConfigureAwait(false);

            try
            {
                _progress.SetCommittingSql($"Committing {entities.Count} rows to Postgres…", entities.Count);
                await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
                foreach (var entity in entities)
                {
                    var existing = await db.Products.FindAsync(entity.Id).ConfigureAwait(false);
                    if (existing is null)
                        db.Products.Add(entity);
                    else
                    {
                        existing.CatalogId = entity.CatalogId;
                        existing.Gender = entity.Gender;
                        existing.MasterCategory = entity.MasterCategory;
                        existing.SubCategory = entity.SubCategory;
                        existing.ArticleType = entity.ArticleType;
                        existing.BaseColour = entity.BaseColour;
                        existing.Season = entity.Season;
                        existing.Year = entity.Year;
                        existing.Usage = entity.Usage;
                        existing.ProductDisplayName = entity.ProductDisplayName;
                        existing.ConcatenatedText = entity.ConcatenatedText;
                        existing.ImageRelPath = entity.ImageRelPath;
                        existing.TextEmbedding = entity.TextEmbedding;
                        existing.ImageEmbedding = entity.ImageEmbedding;
                        existing.UpdatedUtc = entity.UpdatedUtc;
                    }
                }

                await db.SaveChangesAsync().ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await _collections.DeleteByIdsAsync(writtenIds).ConfigureAwait(false);
                throw;
            }

            offset += target;
            _stamp.Save(new IndexStamp(active.Id, active.EmbeddingDim, SigLipModelCatalog.EncodePipelineVersion, offset));

            if (optimizeAfterPatch)
            {
                _progress.SetOptimizing("Optimizing ZVec indexes…");
                _collections.OptimizeBoth();
            }

            sw.Stop();
            _logger.LogInformation(
                "Ingest patch complete — embedded {Encoded}, offset {Offset}/{Total}, {ElapsedMs} ms",
                writtenIds.Count, offset, catalog.Count, sw.ElapsedMilliseconds);
            _progress.SetCompleted(
                $"Patch complete — embedded {target}, offset {offset}/{catalog.Count} ({active.Id}).",
                offset,
                catalog.Count,
                target,
                writtenIds.Count,
                entities.Count,
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (writtenIds.Count > 0)
            {
                try { await _collections.DeleteByIdsAsync(writtenIds).ConfigureAwait(false); }
                catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "ZVec compensation delete failed"); }
            }

            _logger.LogError(ex, "Ingest patch failed");
            _progress.SetFailed("Ingest patch failed", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            _progress.SetRunning(false);
        }
    }
}
