using System.Text.Json;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.Api;

/// <summary>Loads demo wow-query chips from data/wow-queries.json.</summary>
public sealed class WowQueryProvider
{
    private readonly string _path;
    private IReadOnlyList<WowQueryChipDto>? _cache;

    public WowQueryProvider(IOptions<ProductSearchOptions> options)
    {
        _path = options.Value.WowQueriesPath;
    }

    public IReadOnlyList<WowQueryChipDto> Load()
    {
        if (_cache is not null)
            return _cache;

        if (!File.Exists(_path))
            return _cache = [];

        var json = File.ReadAllText(_path);
        var chips = JsonSerializer.Deserialize<List<WowQueryChipDto>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return _cache = chips ?? [];
    }
}
