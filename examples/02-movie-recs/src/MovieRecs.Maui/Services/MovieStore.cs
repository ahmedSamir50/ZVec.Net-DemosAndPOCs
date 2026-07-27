using MovieRecs.Maui.Models;
using ZVec.NET;

namespace MovieRecs.Maui.Services;

/// <summary>
/// Owns the on-disk ZVec movie collection under AppData (<c>zvec-movies</c>).
/// Uses SDK <see cref="IZvecFactory.OpenOrCreate"/> — restart-safe; never obsolete Create.
/// </summary>
public interface IMovieStore
{
    string CollectionPath { get; }
    IZvecCollection<Movie>? Collection { get; }
    void Open();
    void Reset();
    /// <summary>
    /// Merges the flat upsert buffer into the configured HNSW index.
    /// Safe to call on an already-ingested collection without re-embedding.
    /// </summary>
    void Optimize();
}

public sealed class MovieStore : IMovieStore, IDisposable
{
    private readonly IZvecFactory _factory;
    private readonly object _gate = new();
    private IZvecCollection<Movie>? _collection;

    public MovieStore(IZvecFactory factory)
    {
        _factory = factory;
        // Edge-safe path: survives app restarts; wiped only by Reset.
        CollectionPath = Path.Combine(FileSystem.AppDataDirectory, "zvec-movies");
    }

    public string CollectionPath { get; }
    public IZvecCollection<Movie>? Collection
    {
        get
        {
            lock (_gate)
                return _collection;
        }
    }

    public void Open()
    {
        lock (_gate)
            OpenUnlocked();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _collection?.Dispose();
            _collection = null;
            try
            {
                if (Directory.Exists(CollectionPath))
                    Directory.Delete(CollectionPath, recursive: true);
            }
            catch
            {
                // best-effort wipe
            }
            OpenUnlocked();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Upserts stage in a temporary flat (brute-force) buffer for write throughput.
    /// Optimize merges that buffer into HNSW for production-quality ANN.
    /// Reopens afterward so the querier sees merged segments (avoids Gandiva fill_result failures).
    /// </remarks>
    public void Optimize()
    {
        lock (_gate)
        {
            // OpenUnlocked (not Open) — Monitor is not reentrant across separate methods.
            if (_collection is null)
                OpenUnlocked();
            _collection!.Optimize();
            // Fresh handle after segment merge — stale querier can throw InternalError (Query).
            OpenUnlocked();
        }
    }

    private void OpenUnlocked()
    {
        _collection?.Dispose();
        _collection = CollectionBootstrap.OpenOrCreate(_factory, CollectionPath);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _collection?.Dispose();
            _collection = null;
        }
    }
}
