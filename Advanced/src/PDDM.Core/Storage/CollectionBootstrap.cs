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

/// <summary>Open-or-create helper for typed ZVec collections (upstream has no open_or_create).</summary>
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
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            var opened = factory.Open(path, options);
            return new ZVecCollection<T>(opened);
        }

        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            try { Directory.Delete(path); } catch { /* best effort */ }
        }

        var schema = ZVecCollectionSchemaBuilder.From<T>().Build();
        var created = factory.CreateAndOpen(path, schema, options);
        return new ZVecCollection<T>(created);
    }

    /// <summary>Convenience for <see cref="JiraDocChunk"/>.</summary>
    public static IZvecCollection<JiraDocChunk> OpenOrCreateDocs(
        IZvecFactory factory,
        string path,
        bool enableMmap = true)
        => OpenOrCreate<JiraDocChunk>(factory, path, enableMmap);
}
