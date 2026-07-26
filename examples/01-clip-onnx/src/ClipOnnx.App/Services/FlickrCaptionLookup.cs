using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Services;

/// <summary>
/// Secondary UI enrichment: Flickr human captions keyed by image filename.
/// Not used for primary ZVec retrieval — CLIP vision embeddings remain the index.
/// </summary>
public interface IFlickrCaptionLookup
{
    string? GetCaption(string fileName);
    void EnsureLoaded();
}

public sealed class FlickrCaptionLookup : IFlickrCaptionLookup
{
    private readonly ClipOnnxOptions _options;
    private readonly ILogger<FlickrCaptionLookup> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _map;

    public FlickrCaptionLookup(IOptions<ClipOnnxOptions> options, ILogger<FlickrCaptionLookup> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_map is not null)
                return;
            _map = Load();
        }
    }

    public string? GetCaption(string fileName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(fileName) || _map is null)
            return null;
        return _map.TryGetValue(fileName, out var c) ? c : null;
    }

    private Dictionary<string, string> Load()
    {
        var path = Path.Combine(_options.DataRoot, "flickr8k", _options.FlickrCaptionsFile);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            _logger.LogInformation("Caption file not found yet (OK until text zip extract): {Path}", path);
            return map;
        }

        // Format: 1000268201_693b08cb0e.jpg#0\tA child in a pink dress...
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            var key = line[..tab];
            var hash = key.IndexOf('#');
            var file = hash > 0 ? key[..hash] : key;
            if (map.ContainsKey(file))
                continue; // first caption only
            map[file] = line[(tab + 1)..].Trim();
        }

        _logger.LogInformation("Loaded {Count} Flickr captions for UI enrichment", map.Count);
        return map;
    }
}
