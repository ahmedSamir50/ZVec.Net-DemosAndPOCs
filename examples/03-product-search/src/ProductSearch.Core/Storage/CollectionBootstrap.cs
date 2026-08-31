using ZVec.NET;

namespace ProductSearch.Core.Storage;

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
}
