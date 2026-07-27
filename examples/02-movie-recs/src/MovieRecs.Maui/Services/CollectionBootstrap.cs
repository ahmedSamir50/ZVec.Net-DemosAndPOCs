using MovieRecs.Maui.Models;
using ZVec.NET;
using ZVec.NET.Mapping;

namespace MovieRecs.Maui.Services;

/// <summary>
/// Opens (or creates) the typed movie collection via SDK <see cref="IZvecFactory.OpenOrCreate"/>.
/// Prefer this over obsolete Create — restart-safe under AppData.
/// </summary>
public static class CollectionBootstrap
{
    public static IZvecCollection<Movie> OpenOrCreate(IZvecFactory factory, string path, bool enableMmap = true)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var options = new ZVecCollectionOptions { EnableMmap = enableMmap };
        var schema = ZVecCollectionSchemaBuilder.From<Movie>().Build();
        return new ZVecCollection<Movie>(factory.OpenOrCreate(path, schema, options));
    }
}
