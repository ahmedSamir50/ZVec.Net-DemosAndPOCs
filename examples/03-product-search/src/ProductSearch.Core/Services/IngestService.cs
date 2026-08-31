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
    void CancelRunningPatch();
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
    private CancellationTokenSource? _patchCts;

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

            _patchCts?.Cancel();
            _patchCts?.Dispose();
            _patchCts = new CancellationTokenSource();

            var patchIndex = (_stamp.Load().IngestOffset / Math.Max(1, patchSize)) + 1;
            _progress.ResetForPatch(patchSize, patchIndex);
            _progress.AppendEvent("Info", "start", $"Patch {patchIndex} started (size {patchSize})");
            _logger.LogInformation("Starting ingest patch {PatchIndex} size {PatchSize}", patchIndex, patchSize);
            var ct = _patchCts.Token;
            _ = Task.Run(() => RunPatchAsync(patchSize, request.OptimizeAfterPatch, ct), ct);
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
                using (var db = _dbFactory.CreateDbContext())
                    db.ClearEmbeddings(active.EmbeddingDim);
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

    public void CancelRunningPatch()
    {
        lock (_startGate)
        {
            _patchCts?.Cancel();
        }
    }

    private async Task RunPatchAsync(int patchSize, bool optimizeAfterPatch, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chunkSize = Math.Clamp(_options.IngestChunkSize, 1, patchSize);
        var totalEncoded = 0;
        var totalZvec = 0;
        var totalSql = 0;

        try
        {
            var active = _models.ActiveDefinition;
            _logger.LogInformation("Ingest patch: ensuring catalog CSV from pack…");
            _progress.AppendEvent("Info", "catalog", "Ensuring catalog CSV from pack…");
            await _downloader.EnsureStylesCsvAsync(ct).ConfigureAwait(false);
            var catalogTotal = await _catalogReader.GetCatalogTotalAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Ingest patch: catalog has {Count} rows", catalogTotal);
            _progress.AppendEvent("Info", "catalog", $"Catalog ready — {catalogTotal} products");

            var stamp = _stamp.Load();
            var offset = Math.Clamp(stamp.IngestOffset, 0, catalogTotal);

            await using (var dbCheck = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var sqlCount = await dbCheck.Products.CountAsync(ct).ConfigureAwait(false);
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

            var remaining = Math.Max(0, catalogTotal - offset);
            var target = Math.Min(patchSize, remaining);
            if (target == 0)
            {
                _progress.AppendEvent("Info", "complete", $"Caught up — offset {offset}/{catalogTotal}");
                _progress.SetCompleted($"Caught up — offset {offset}/{catalogTotal}.", offset, catalogTotal, 0, 0, 0, sw.ElapsedMilliseconds);
                _logger.LogInformation("Ingest patch: nothing left to ingest at offset {Offset}/{Total}", offset, catalogTotal);
                return;
            }

            var chunkCount = (target + chunkSize - 1) / chunkSize;
            _logger.LogInformation(
                "Ingest patch: {Target} products in {Chunks} sub-batches of {ChunkSize} starting at offset {Offset}/{Total}",
                target, chunkCount, chunkSize, offset, catalogTotal);

            var processedInPatch = 0;

            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var chunkSw = System.Diagnostics.Stopwatch.StartNew();

                var thisChunkSize = Math.Min(chunkSize, target - processedInPatch);
                var slice = await _catalogReader.ReadSliceAsync(offset, thisChunkSize, ct).ConfigureAwait(false);
                if (slice.Count == 0)
                    break;

                var chunkIds = new List<string>(slice.Count);
                var textBatch = new List<(string Id, string ConcatenatedText, string Gender, string BaseColour, string Season, string Usage, string MasterCategory, float[] Embedding)>(slice.Count);
                var imageBatch = new List<(string Id, float[] Embedding)>(slice.Count);
                var catalog = new List<ProductEntity>(slice.Count);
                var embeddings = new List<ProductEmbeddingWrite>(slice.Count);

                _progress.SetEncoding(
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · prefetching {slice.Count} images…",
                    offset,
                    catalogTotal,
                    totalEncoded);
                _progress.AppendEvent("Info", "prefetch",
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · prefetching {slice.Count} images");

                var imagePaths = await _downloader.PrefetchImagesAsync(slice.Select(p => p.CatalogId), ct).ConfigureAwait(false);

                var ready = new List<(CatalogProduct Product, string ImagePath)>(slice.Count);
                for (var i = 0; i < slice.Count; i++)
                {
                    var product = slice[i];
                    if (!imagePaths.TryGetValue(product.CatalogId.Trim(), out var imagePath))
                    {
                        _logger.LogWarning("Skipping catalog id {CatalogId} — image not found in pack.", product.CatalogId);
                        continue;
                    }

                    ready.Add((product, imagePath));
                }

                if (ready.Count == 0)
                {
                    offset += slice.Count;
                    processedInPatch += slice.Count;
                    continue;
                }

                _progress.SetEncoding(
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · encoding {ready.Count} products…",
                    offset,
                    catalogTotal,
                    totalEncoded + ready.Count);
                _progress.AppendEvent("Info", "encode",
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · encoding {ready.Count} products");

                var texts = ready.Select(r => r.Product.ConcatenatedText).ToList();
                var paths = ready.Select(r => r.ImagePath).ToList();
                var textTask = Task.Run(() => _encoder.EncodeTextBatch(texts, ct), ct);
                var imageTask = Task.Run(() => _encoder.EncodeImageBatch(paths, ct), ct);
                await Task.WhenAll(textTask, imageTask).ConfigureAwait(false);
                var textEmbeddings = await textTask.ConfigureAwait(false);
                var imageEmbeddings = await imageTask.ConfigureAwait(false);

                if (textEmbeddings.Length != ready.Count || imageEmbeddings.Length != ready.Count)
                {
                    throw new InvalidOperationException(
                        $"Encoder batch size mismatch: ready={ready.Count} text={textEmbeddings.Length} image={imageEmbeddings.Length}.");
                }

                var now = DateTimeOffset.UtcNow;
                for (var i = 0; i < ready.Count; i++)
                {
                    var product = ready[i].Product;
                    var id = ProductIdGenerator.StringFromCatalogId(product.CatalogId);
                    var textEmbedding = textEmbeddings[i];
                    var imageEmbedding = imageEmbeddings[i];

                    textBatch.Add((id, product.ConcatenatedText, product.Gender, product.BaseColour, product.Season, product.Usage, product.MasterCategory, textEmbedding));
                    imageBatch.Add((id, imageEmbedding));
                    var productId = Guid.Parse(id);
                    catalog.Add(new ProductEntity
                    {
                        Id = productId,
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
                        UpdatedUtc = now
                    });
                    embeddings.Add(new ProductEmbeddingWrite(
                        productId,
                        new Vector(textEmbedding),
                        new Vector(imageEmbedding),
                        now));
                    chunkIds.Add(id);
                }

                _progress.SetUpsertingZVec(
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · ZVec upserting {chunkIds.Count} docs…",
                    totalZvec + chunkIds.Count);
                _progress.AppendEvent("Info", "encode",
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · encoded {chunkIds.Count} products");
                _logger.LogInformation("Ingest sub-batch {Chunk}/{Total}: upserting {Count} vectors to ZVec", chunkIndex + 1, chunkCount, chunkIds.Count);

                try
                {
                    await Task.WhenAll(
                        _collections.UpsertTextBatchAsync(textBatch, ct),
                        _collections.UpsertImageBatchAsync(imageBatch, ct)).ConfigureAwait(false);
                }
                catch
                {
                    _progress.AppendEvent("Error", "zvec",
                        $"Sub-batch {chunkIndex + 1}/{chunkCount} · ZVec failed — rolling back chunk");
                    await _collections.DeleteByIdsAsync(chunkIds, ct).ConfigureAwait(false);
                    throw;
                }
                _progress.AppendEvent("Info", "zvec",
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · ZVec upserted {chunkIds.Count} docs");

                try
                {
                    _progress.SetCommittingSql(
                        $"Sub-batch {chunkIndex + 1}/{chunkCount} · SQL committing {catalog.Count} rows…",
                        totalSql + catalog.Count);
                    await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                    await ProductBulkUpsert.UpsertChunkAsync(db, catalog, embeddings, active.EmbeddingDim, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    _progress.AppendEvent("Error", "sql",
                        $"Sub-batch {chunkIndex + 1}/{chunkCount} · SQL failed — rolling back ZVec chunk");
                    await _collections.DeleteByIdsAsync(chunkIds, ct).ConfigureAwait(false);
                    throw;
                }

                offset += slice.Count;
                processedInPatch += slice.Count;
                totalEncoded += chunkIds.Count;
                totalZvec += chunkIds.Count;
                totalSql += catalog.Count;

                _stamp.Save(new IndexStamp(active.Id, active.EmbeddingDim, SigLipModelCatalog.EncodePipelineVersion, offset));
                chunkSw.Stop();
                _progress.AppendEvent("Info", "commit",
                    $"Sub-batch {chunkIndex + 1}/{chunkCount} · SQL committed · offset {offset}/{catalogTotal}",
                    chunkSw.ElapsedMilliseconds);
                _logger.LogInformation(
                    "Ingest sub-batch {Chunk}/{Total} committed — offset now {Offset}/{CatalogTotal}",
                    chunkIndex + 1, chunkCount, offset, catalogTotal);

                textBatch.Clear();
                imageBatch.Clear();
                catalog.Clear();
                embeddings.Clear();
                chunkIds.Clear();
            }

            if (optimizeAfterPatch)
            {
                _progress.SetOptimizing("Optimizing ZVec indexes…");
                _progress.AppendEvent("Info", "optimize", "Optimizing ZVec HNSW indexes…");
                _collections.OptimizeBoth();
                _progress.AppendEvent("Info", "optimize", "ZVec indexes optimized");
            }

            sw.Stop();
            _logger.LogInformation(
                "Ingest patch complete — embedded {Encoded}, offset {Offset}/{Total}, {ElapsedMs} ms",
                totalEncoded, offset, catalogTotal, sw.ElapsedMilliseconds);
            _progress.AppendEvent("Info", "complete",
                $"Patch complete — embedded {totalEncoded}, offset {offset}/{catalogTotal}",
                sw.ElapsedMilliseconds);
            _progress.SetCompleted(
                $"Patch complete — embedded {totalEncoded}, offset {offset}/{catalogTotal} ({active.Id}).",
                offset,
                catalogTotal,
                totalEncoded,
                totalZvec,
                totalSql,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Ingest patch cancelled");
            _progress.AppendEvent("Warn", "cancel", "Ingest patch cancelled");
            _progress.SetFailed("Ingest cancelled", "The ingest patch was cancelled.");
        }
        catch (Exception ex)
        {
            var (summary, detail) = BootstrapExceptionFormatter.Format(ex);
            _logger.LogError(ex, "Ingest patch failed: {Summary}", summary);
            _progress.AppendEvent("Error", "failed", detail);
            _progress.SetFailed("Ingest patch failed", detail);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            _progress.SetRunning(false);
        }
    }
}
