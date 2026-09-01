using ClipOnnx.App.DataModels;
using ZVec.NET;

namespace ClipOnnx.App.Storage;

/// <summary>
/// Typed ZVec open-or-create for gallery entities via SDK
/// <see cref="IZvecFactory.OpenOrCreate"/> — package README “Create vs Open (restart-safe collections)”.
/// Schema/metric come from entity attributes — not CLIP-specific logic.
/// </summary>
public static class CollectionBootstrap
{
    public static IZvecCollection<T> OpenOrCreate<T>(
        IZvecFactory factory,
        string path,
        bool enableMmap = true)
        where T : class, new()
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

    public static IZvecCollection<ImageAsset> OpenOrCreateGallery(
        IZvecFactory factory,
        string path,
        bool enableMmap = true)
        => OpenOrCreate<ImageAsset>(factory, path, enableMmap);
}
