using System.Text.Json;
using System.Text.Json.Serialization;
using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Services;

/// <summary>
/// Persisted gallery stamp in <c>data/state/flickr8k.json</c>.
/// Ties the on-disk ZVec index to a specific CLIP model id + embedding dim + preprocess version.
/// After the user switches ONNX models in the UI, a mismatch blocks search/ingest until Reset+Ingest
/// — different CLIP variants must never share one vector space.
/// </summary>
public sealed record GalleryStamp(
    int Offset,
    string? ModelId = null,
    int? EmbeddingDim = null,
    string? EncodePipelineVersion = null);

public interface IGalleryStampStore
{
    string StatePath { get; }
    GalleryStamp Load();
    void Save(GalleryStamp stamp);
    /// <summary>True when index has progress/vectors that do not match the active model.</summary>
    bool IsMismatch(ClipModelDefinition active, GalleryStamp? stamp = null);
    string? MismatchMessage(ClipModelDefinition active, GalleryStamp? stamp = null);
}

public sealed class GalleryStampStore : IGalleryStampStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ClipOnnxOptions _options;
    private readonly object _gate = new();

    public GalleryStampStore(IOptions<ClipOnnxOptions> options)
    {
        _options = options.Value;
    }

    public string StatePath => Path.GetFullPath(Path.Combine(_options.DataRoot, "state", "flickr8k.json"));

    public GalleryStamp Load()
    {
        lock (_gate)
        {
            var path = StatePath;
            if (!File.Exists(path))
                return new GalleryStamp(0);

            try
            {
                using var fs = File.OpenRead(path);
                return JsonSerializer.Deserialize<GalleryStamp>(fs, JsonOpts) ?? new GalleryStamp(0);
            }
            catch
            {
                return new GalleryStamp(0);
            }
        }
    }

    public void Save(GalleryStamp stamp)
    {
        lock (_gate)
        {
            var path = StatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(stamp, JsonOpts);
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public bool IsMismatch(ClipModelDefinition active, GalleryStamp? stamp = null)
    {
        stamp ??= Load();
        // Empty index: no mismatch (ready to ingest with active model).
        if (stamp.Offset <= 0
            && string.IsNullOrWhiteSpace(stamp.ModelId)
            && stamp.EmbeddingDim is null)
            return false;

        if (!string.Equals(stamp.ModelId, active.Id, StringComparison.OrdinalIgnoreCase))
            return true;
        if (stamp.EmbeddingDim is int d && d != active.EmbeddingDim)
            return true;
        if (!string.IsNullOrWhiteSpace(stamp.EncodePipelineVersion)
            && !string.Equals(stamp.EncodePipelineVersion, ClipModelCatalog.EncodePipelineVersion, StringComparison.Ordinal))
            return true;

        // Legacy state: Offset > 0 but no ModelId → treat as stale (unknown model).
        if (stamp.Offset > 0 && string.IsNullOrWhiteSpace(stamp.ModelId))
            return true;

        return false;
    }

    public string? MismatchMessage(ClipModelDefinition active, GalleryStamp? stamp = null)
    {
        if (!IsMismatch(active, stamp))
            return null;

        stamp ??= Load();
        var oldModel = string.IsNullOrWhiteSpace(stamp.ModelId) ? "(unknown / legacy)" : stamp.ModelId;
        var oldDim = stamp.EmbeddingDim?.ToString() ?? "?";
        return
            $"Index was built with {oldModel} ({oldDim}-d). Active model is {active.DisplayName} ({active.EmbeddingDim}-d). " +
            "Reset index and re-embed before searching or ingesting.";
    }
}
