using Microsoft.Extensions.Logging;
using ZVec.NET;

namespace ProductSearch.Core.Storage;

public static class CollectionBootstrap
{
    public static IZvecCollection<T> OpenOrCreate<T>(
        IZvecFactory factory,
        string path,
        bool enableMmap = true,
        ILogger? logger = null)
        where T : class, new()
    {
        var wiped = false;
        return ZVecCollectionOpenHelper.OpenOrCreateWithRecovery<T>(factory, path, enableMmap, logger, ref wiped);
    }
}
