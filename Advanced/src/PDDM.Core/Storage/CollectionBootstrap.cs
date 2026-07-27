using PDDM.Core.Models;
using ZVec.NET;
using ZVec.NET.Mapping;

namespace PDDM.Core.Storage;

/// <summary>
/// Holds the live docs collection so ingest can wipe and reopen without restarting the process.
/// </summary>
public sealed class DocsCollectionHolder
{
    private readonly IZvecFactory _factory;
    private readonly object _gate = new();
    private IZvecCollection<JiraDocChunk> _collection;
    private string _path;
    private bool _enableMmap;

    /// <summary>Creates the holder with an initially opened collection.</summary>
    public DocsCollectionHolder(IZvecFactory factory, string path, bool enableMmap = true)
    {
        _factory = factory;
        _path = path;
        _enableMmap = enableMmap;
        _collection = CollectionBootstrap.OpenOrCreateDocs(factory, path, enableMmap);
    }

    /// <summary>Current typed collection (may change after <see cref="Recreate"/>).</summary>
    public IZvecCollection<JiraDocChunk> Collection
    {
        get
        {
            lock (_gate)
                return _collection;
        }
    }

    /// <summary>Deletes the on-disk collection and opens a fresh empty one.</summary>
    public void Recreate(string? path = null, bool? enableMmap = null)
    {
        lock (_gate)
        {
            if (path is not null)
                _path = path;
            if (enableMmap is not null)
                _enableMmap = enableMmap.Value;

            DisposeCollection(_collection);

            if (Directory.Exists(_path))
            {
                try
                {
                    Directory.Delete(_path, recursive: true);
                }
                catch (IOException)
                {
                    // Retry once after brief pause (mmap unlock).
                    Thread.Sleep(200);
                    Directory.Delete(_path, recursive: true);
                }
            }

            _collection = CollectionBootstrap.OpenOrCreateDocs(_factory, _path, _enableMmap);
        }
    }

    /// <summary>
    /// Dispose + OpenOrCreate the same path (no wipe). Use after Optimize so Query sees merged segments.
    /// </summary>
    public void Reopen()
    {
        lock (_gate)
        {
            DisposeCollection(_collection);
            _collection = CollectionBootstrap.OpenOrCreateDocs(_factory, _path, _enableMmap);
        }
    }

    private static void DisposeCollection(IZvecCollection<JiraDocChunk> collection)
    {
        if (collection is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { /* best effort */ }
            return;
        }

        if (collection is IAsyncDisposable asyncDisposable)
        {
            try { asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Typed ZVec open-or-create via SDK <see cref="IZvecFactory.OpenOrCreate"/> (beta.3+).
/// Aligns with package README “Create vs Open (restart-safe collections)”:
/// prefer <c>OpenOrCreate</c> over <c>CreateAndOpen</c>/<c>Open</c> branching;
/// DI default is <c>ZVecCollectionOpenMode.OpenOrCreate</c> (obsolete <c>Create</c> bool).
/// </summary>
public static class CollectionBootstrap
{
    /// <summary>Opens an existing collection or creates one from <typeparamref name="T"/> schema.</summary>
    public static IZvecCollection<T> OpenOrCreate<T>(
        IZvecFactory factory,
        string path,
        bool enableMmap = true)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var options = new ZVecCollectionOptions { EnableMmap = enableMmap };
        var schema = ZVecCollectionSchemaBuilder.From<T>().Build();
        return new ZVecCollection<T>(factory.OpenOrCreate(path, schema, options));
    }

    /// <summary>Convenience for <see cref="JiraDocChunk"/>.</summary>
    public static IZvecCollection<JiraDocChunk> OpenOrCreateDocs(
        IZvecFactory factory,
        string path,
        bool enableMmap = true)
        => OpenOrCreate<JiraDocChunk>(factory, path, enableMmap);
}
