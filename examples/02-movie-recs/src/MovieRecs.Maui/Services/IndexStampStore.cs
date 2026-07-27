using System.Text.Json;
using System.Text.Json.Serialization;
using MovieRecs.Maui.Options;

namespace MovieRecs.Maui.Services;

/// <summary>
/// On-disk identity of the movie index: count + model id + dim + encode pipeline version.
/// Prevents mixing embedding spaces after a model/pipeline bump without an explicit Reset.
/// </summary>
public sealed record IndexStamp(
    int Count,
    string? ModelId = null,
    int? EmbeddingDim = null,
    string? EncodePipelineVersion = null);

public interface IIndexStampStore
{
    string StatePath { get; }
    IndexStamp Load();
    void Save(IndexStamp stamp);
    bool IsReady(IndexStamp? stamp = null);
    bool IsMismatch(IndexStamp? stamp = null);
}

/// <summary>
/// JSON stamp under AppData/state. <see cref="IsReady"/> short-circuits ingest;
/// <see cref="IsMismatch"/> forces wipe before re-embed.
/// </summary>
public sealed class IndexStampStore : IIndexStampStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    public string StatePath =>
        Path.Combine(FileSystem.AppDataDirectory, "state", "movielens-stamp.json");

    public IndexStamp Load()
    {
        lock (_gate)
        {
            var path = StatePath;
            if (!File.Exists(path))
                return new IndexStamp(0);

            try
            {
                using var fs = File.OpenRead(path);
                return JsonSerializer.Deserialize<IndexStamp>(fs, JsonOpts) ?? new IndexStamp(0);
            }
            catch
            {
                return new IndexStamp(0);
            }
        }
    }

    public void Save(IndexStamp stamp)
    {
        lock (_gate)
        {
            var path = StatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(stamp, JsonOpts));
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public bool IsReady(IndexStamp? stamp = null)
    {
        stamp ??= Load();
        return stamp.Count > 0
               && !IsMismatch(stamp)
               && string.Equals(stamp.ModelId, MovieRecsOptions.ModelId, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsMismatch(IndexStamp? stamp = null)
    {
        stamp ??= Load();
        if (stamp.Count <= 0 && string.IsNullOrWhiteSpace(stamp.ModelId))
            return false;

        if (!string.Equals(stamp.ModelId, MovieRecsOptions.ModelId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (stamp.EmbeddingDim is int d && d != MovieRecsOptions.EmbeddingDim)
            return true;
        // Pipeline version (e.g. seq256) must match — old indexes are incompatible.
        if (!string.IsNullOrWhiteSpace(stamp.EncodePipelineVersion)
            && !string.Equals(stamp.EncodePipelineVersion, MovieRecsOptions.EncodePipelineVersion, StringComparison.Ordinal))
            return true;
        return false;
    }
}
