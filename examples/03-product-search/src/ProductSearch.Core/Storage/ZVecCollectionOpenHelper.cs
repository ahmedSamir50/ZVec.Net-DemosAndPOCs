using Microsoft.Extensions.Logging;
using ZVec.NET;
using ZVec.NET.Exceptions;

namespace ProductSearch.Core.Storage;

internal static class ZVecCollectionOpenHelper
{
    private static readonly int[] OpenBackoffMs = [50, 150, 400];

    public static bool IsLockOpenFailure(Exception ex)
    {
        if (ex is not ZVecNativeException)
            return ContainsLockOpenSignal(ex.Message);

        return ContainsLockOpenSignal(ex.Message);
    }

    private static bool ContainsLockOpenSignal(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("lock file", StringComparison.OrdinalIgnoreCase)
               || message.Contains("InternalError (Open)", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Store files exist but RocksDB LOCK is missing — usually from manual LOCK deletion.</summary>
    public static bool IsCorruptMissingLock(string collectionPath)
    {
        if (!Directory.Exists(collectionPath))
            return false;

        var lockPath = Path.Combine(collectionPath, "LOCK");
        if (File.Exists(lockPath))
            return false;

        return Directory.EnumerateFileSystemEntries(collectionPath).Any();
    }

    /// <summary>Another process (or stale OS handle) holds the LOCK file exclusively.</summary>
    public static bool IsLockHeldByOtherProcess(string collectionPath)
    {
        var lockPath = Path.Combine(collectionPath, "LOCK");
        if (!File.Exists(lockPath))
            return false;

        try
        {
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static IZvecCollection<T> OpenOrCreateWithRecovery<T>(
        IZvecFactory factory,
        string path,
        bool enableMmap,
        ILogger? logger,
        ref bool wipedAny)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Directory.CreateDirectory(path);

        try
        {
            return OpenWithBackoff<T>(factory, path, enableMmap);
        }
        catch (Exception ex) when (IsLockOpenFailure(ex))
        {
            if (IsLockHeldByOtherProcess(path))
            {
                throw new InvalidOperationException(
                    $"ZVec collection at '{path}' is locked by another process. " +
                    "Stop any other ProductSearch.Api instance and retry.",
                    ex);
            }

            if (!IsCorruptMissingLock(path))
                throw;

            logger?.LogWarning(
                "ZVec store at {Path} is missing LOCK with leftover data — wiping corrupt store and recreating",
                path);

            TryDeleteDir(path);
            Directory.CreateDirectory(path);
            wipedAny = true;

            return OpenWithBackoff<T>(factory, path, enableMmap);
        }
    }

    private static IZvecCollection<T> OpenWithBackoff<T>(
        IZvecFactory factory,
        string path,
        bool enableMmap)
        where T : class, new()
    {
        Exception? last = null;
        foreach (var delayMs in OpenBackoffMs)
        {
            try
            {
                return OpenCore<T>(factory, path, enableMmap);
            }
            catch (Exception ex) when (IsLockOpenFailure(ex))
            {
                last = ex;
                Thread.Sleep(delayMs);
            }
        }

        try
        {
            return OpenCore<T>(factory, path, enableMmap);
        }
        catch (Exception ex) when (IsLockOpenFailure(ex) && last is not null)
        {
            throw new InvalidOperationException(
                $"ZVec could not open collection at '{path}' after release backoff. " +
                "Another ProductSearch.Api instance may still be running.",
                ex);
        }
    }

    private static IZvecCollection<T> OpenCore<T>(
        IZvecFactory factory,
        string path,
        bool enableMmap)
        where T : class, new()
    {
        var options = new ZVecCollectionOptions { EnableMmap = enableMmap };
        var schema = ZVecCollectionSchemaBuilder.From<T>().Build();
        return new ZVecCollection<T>(factory.OpenOrCreate(path, schema, options));
    }

    public static void TryDeleteDir(string path)
    {
        if (!Directory.Exists(path))
            return;

        try { Directory.Delete(path, recursive: true); }
        catch
        {
            Thread.Sleep(150);
            try { Directory.Delete(path, recursive: true); } catch { /* ignore */ }
        }
    }
}
