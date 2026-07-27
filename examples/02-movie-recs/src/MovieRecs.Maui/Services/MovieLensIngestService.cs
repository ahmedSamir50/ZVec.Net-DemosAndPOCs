using MovieRecs.Maui.Encoding;
using MovieRecs.Maui.Models;
using MovieRecs.Maui.Options;

namespace MovieRecs.Maui.Services;

public interface IMovieLensIngestService
{
    Task<bool> IngestAsync(CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);
    /// <summary>Run <c>Optimize()</c> on the open collection (no re-embed). For already-ingested indexes.</summary>
    Task OptimizeAsync(CancellationToken ct = default);
}

/// <summary>
/// Downloads/parses MovieLens (from MauiAssets), embeds with MiniLM, upserts into ZVec, then Optimizes.
/// Stamp short-circuits when the on-disk index already matches model/pipeline version.
/// </summary>
public sealed class MovieLensIngestService : IMovieLensIngestService
{
    private readonly IMiniLmEncoder _encoder;
    private readonly IMovieLensCatalog _catalog;
    private readonly IMovieStore _store;
    private readonly IIndexStampStore _stamp;
    private readonly IngestProgressStatus _progress;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public MovieLensIngestService(
        IMiniLmEncoder encoder,
        IMovieLensCatalog catalog,
        IMovieStore store,
        IIndexStampStore stamp,
        IngestProgressStatus progress)
    {
        _encoder = encoder;
        _catalog = catalog;
        _store = store;
        _stamp = stamp;
        _progress = progress;
    }

    public async Task<bool> IngestAsync(CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
            return false;

        try
        {
            _progress.Begin("Loading catalog…", 0);
            await _catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);
            await _encoder.EnsureLoadedAsync(ct).ConfigureAwait(false);

            var stamp = _stamp.Load();
            // Already indexed with this model/pipeline — open and skip re-encode.
            if (_stamp.IsReady(stamp))
            {
                _store.Open();
                _progress.Begin("Ready", stamp.Count);
                _progress.Complete();
                return true;
            }

            // Mismatch or partial stamp → wipe so we never mix embedding spaces.
            if (_stamp.IsMismatch(stamp) || stamp.Count > 0)
                _store.Reset();
            else
                _store.Open();

            var collection = _store.Collection
                ?? throw new InvalidOperationException("ZVec collection failed to open.");

            var movies = _catalog.Movies;
            _progress.Begin("Embedding movies…", movies.Count);

            const int reportEvery = 25;
            for (var i = 0; i < movies.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = movies[i];
                var emb = _encoder.Embed(info.EmbedText);
                await collection.UpsertAsync(new Movie
                {
                    Id = info.Id,
                    Title = info.Title,
                    Genres = info.Genres,
                    Year = info.Year,
                    Embedding = emb
                }, ct).ConfigureAwait(false);

                if (i % reportEvery == 0 || i == movies.Count - 1)
                    _progress.Report(i + 1);
            }

            // Upserts land in a flat buffer; Optimize merges into HNSW (correct default for future clones).
            _progress.Begin("Optimizing index…", movies.Count);
            _progress.Report(movies.Count, "Optimizing index…");
            collection.Optimize();

            _stamp.Save(new IndexStamp(
                Count: movies.Count,
                ModelId: MovieRecsOptions.ModelId,
                EmbeddingDim: MovieRecsOptions.EmbeddingDim,
                EncodePipelineVersion: MovieRecsOptions.EncodePipelineVersion));

            _progress.Complete();
            return true;
        }
        catch (OperationCanceledException)
        {
            _progress.Fail("Ingest cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _progress.Fail(ex.Message);
            return false;
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Opens the collection if needed, then merges the flat upsert buffer into HNSW.</remarks>
    public Task OptimizeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store.Optimize();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        _store.Reset();
        _stamp.Save(new IndexStamp(0));
        _progress.Reset();
        return Task.CompletedTask;
    }
}
