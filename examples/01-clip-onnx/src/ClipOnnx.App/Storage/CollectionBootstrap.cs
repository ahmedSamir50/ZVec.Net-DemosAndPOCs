using ClipOnnx.App.Models;
using ZVec.NET;
using ZVec.NET.Mapping;

namespace ClipOnnx.App.Storage;

/// <summary>
/// Open-or-create typed ZVec collection for <see cref="Models.ImageAsset"/> (CreateAndOpen throws if path exists).
/// Schema/metric come from the entity attributes — not CLIP-specific logic.
/// </summary>
public static class CollectionBootstrap
{
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
            return new ZVecCollection<T>(factory.Open(path, options));

        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            try { Directory.Delete(path); } catch { /* best effort */ }
        }

        var schema = ZVecCollectionSchemaBuilder.From<T>().Build();
        return new ZVecCollection<T>(factory.CreateAndOpen(path, schema, options));
    }

    public static IZvecCollection<ImageAsset> OpenOrCreateGallery(
        IZvecFactory factory,
        string path,
        bool enableMmap = true)
        => OpenOrCreate<ImageAsset>(factory, path, enableMmap);
}
