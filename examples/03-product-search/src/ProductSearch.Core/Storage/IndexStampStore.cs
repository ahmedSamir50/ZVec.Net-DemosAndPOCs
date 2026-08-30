using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;

namespace ProductSearch.Core.Storage;

public interface IIndexStampStore
{
    string StatePath { get; }
    IndexStamp Load();
    void Save(IndexStamp stamp);
    bool IsMismatch(SigLipModelDefinition active, IndexStamp? stamp = null);
    string? MismatchMessage(SigLipModelDefinition active, IndexStamp? stamp = null);
}

public sealed class IndexStampStore : IIndexStampStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ProductSearchOptions _options;
    private readonly object _gate = new();

    public IndexStampStore(IOptions<ProductSearchOptions> options)
    {
        _options = options.Value;
    }

    public string StatePath => Path.Combine(_options.DataRoot, "state", "product-search-stamp.json");

    public IndexStamp Load()
    {
        lock (_gate)
        {
            if (!File.Exists(StatePath))
                return new IndexStamp("", 0, SigLipModelCatalog.EncodePipelineVersion, 0);

            try
            {
                using var fs = File.OpenRead(StatePath);
                return JsonSerializer.Deserialize<IndexStamp>(fs, JsonOpts)
                       ?? new IndexStamp("", 0, SigLipModelCatalog.EncodePipelineVersion, 0);
            }
            catch
            {
                return new IndexStamp("", 0, SigLipModelCatalog.EncodePipelineVersion, 0);
            }
        }
    }

    public void Save(IndexStamp stamp)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(stamp, JsonOpts));
            File.Copy(tmp, StatePath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public bool IsMismatch(SigLipModelDefinition active, IndexStamp? stamp = null)
    {
        stamp ??= Load();
        if (stamp.IngestOffset <= 0 && string.IsNullOrWhiteSpace(stamp.ModelId))
            return false;

        if (!string.Equals(stamp.ModelId, active.Id, StringComparison.OrdinalIgnoreCase))
            return true;
        if (stamp.EmbeddingDim > 0 && stamp.EmbeddingDim != active.EmbeddingDim)
            return true;
        if (!string.IsNullOrWhiteSpace(stamp.EncodePipelineVersion)
            && !string.Equals(stamp.EncodePipelineVersion, SigLipModelCatalog.EncodePipelineVersion, StringComparison.Ordinal))
            return true;
        if (stamp.IngestOffset > 0 && string.IsNullOrWhiteSpace(stamp.ModelId))
            return true;

        return false;
    }

    public string? MismatchMessage(SigLipModelDefinition active, IndexStamp? stamp = null)
    {
        if (!IsMismatch(active, stamp))
            return null;

        stamp ??= Load();
        var oldModel = string.IsNullOrWhiteSpace(stamp.ModelId) ? "(unknown / legacy)" : stamp.ModelId;
        var oldDim = stamp.EmbeddingDim > 0 ? stamp.EmbeddingDim.ToString() : "?";
        return
            $"Index was built with {oldModel} ({oldDim}-d). Active model is {active.DisplayName} ({active.EmbeddingDim}-d). " +
            "Reset indexes and re-ingest before searching.";
    }
}
