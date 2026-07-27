using System.Globalization;
using System.Text.RegularExpressions;
using MovieRecs.Maui.Models;
using MovieRecs.Maui.Options;

namespace MovieRecs.Maui.Services;

public interface IMovieLensCatalog
{
    Task EnsureLoadedAsync(CancellationToken ct = default);
    IReadOnlyList<MovieInfo> Movies { get; }
    IReadOnlyDictionary<string, MovieInfo> ById { get; }
    IReadOnlyList<UserSummary> Users { get; }
    IReadOnlyList<MovieInfo> Search(string? query, int take = 20);
    IReadOnlyList<MovieInfo> LikesForUser(int userId);
    MovieInfo? FindByTitleContains(string fragment);
}

/// <summary>
/// Parses MovieLens CSVs from MauiAssets into an in-memory catalog + per-user like lists
/// (rating ≥ <see cref="MovieRecsOptions.MinLikeRating"/>). Used for search UI and behaviour vectors.
/// </summary>
public sealed class MovieLensCatalog : IMovieLensCatalog
{
    private static readonly Regex YearRx = new(@"\((\d{4})\)\s*$", RegexOptions.Compiled);
    private readonly MovieRecsOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<MovieInfo> _movies = [];
    private Dictionary<string, MovieInfo> _byId = new(StringComparer.Ordinal);
    private Dictionary<int, List<string>> _likes = new();
    private List<UserSummary> _users = [];
    private bool _loaded;

    public MovieLensCatalog(MovieRecsOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<MovieInfo> Movies => _movies;
    public IReadOnlyDictionary<string, MovieInfo> ById => _byId;
    public IReadOnlyList<UserSummary> Users => _users;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return;

            var cache = Path.Combine(FileSystem.CacheDirectory, "movielens");
            Directory.CreateDirectory(cache);
            var moviesPath = await CopyAssetAsync(MovieRecsOptions.MoviesAssetPath, cache, ct).ConfigureAwait(false);
            var ratingsPath = await CopyAssetAsync(MovieRecsOptions.RatingsAssetPath, cache, ct).ConfigureAwait(false);

            _movies = ParseMovies(moviesPath);
            _byId = _movies.ToDictionary(m => m.Id, StringComparer.Ordinal);
            _likes = ParseLikes(ratingsPath, _options.MinLikeRating);
            _users = _likes
                .Select(kv => new UserSummary(kv.Key, kv.Value.Count))
                .Where(u => u.LikeCount >= 5)
                .OrderByDescending(u => u.LikeCount)
                .Take(40)
                .ToList();
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<MovieInfo> Search(string? query, int take = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _movies.Take(take).ToList();

        var q = query.Trim();
        return _movies
            .Where(m => m.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .ToList();
    }

    public IReadOnlyList<MovieInfo> LikesForUser(int userId)
    {
        if (!_likes.TryGetValue(userId, out var ids))
            return [];
        return ids.Select(id => _byId.GetValueOrDefault(id)).Where(m => m is not null).Cast<MovieInfo>().ToList();
    }

    public MovieInfo? FindByTitleContains(string fragment) =>
        _movies.FirstOrDefault(m => m.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static List<MovieInfo> ParseMovies(string path)
    {
        var list = new List<MovieInfo>(10_000);
        using var reader = new StreamReader(path);
        _ = reader.ReadLine(); // header
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            // movieId,title,genres — title may contain commas when quoted
            if (!TrySplitCsv3(line, out var id, out var title, out var genres))
                continue;
            int? year = null;
            var m = YearRx.Match(title);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var y))
                year = y;
            list.Add(new MovieInfo(id, title, genres, year));
        }
        return list;
    }

    private static Dictionary<int, List<string>> ParseLikes(string path, float minRating)
    {
        var map = new Dictionary<int, List<string>>();
        using var reader = new StreamReader(path);
        _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < 3)
                continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
                continue;
            var movieId = parts[1].Trim();
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
                continue;
            if (rating < minRating)
                continue;
            if (!map.TryGetValue(userId, out var list))
            {
                list = [];
                map[userId] = list;
            }
            list.Add(movieId);
        }
        return map;
    }

    private static bool TrySplitCsv3(string line, out string a, out string b, out string c)
    {
        a = b = c = "";
        var first = line.IndexOf(',');
        if (first < 0)
            return false;
        a = line[..first].Trim();
        var rest = line[(first + 1)..];
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf("\",", StringComparison.Ordinal);
            if (end < 0)
                return false;
            b = rest[1..end].Replace("\"\"", "\"");
            c = rest[(end + 2)..].Trim();
            return true;
        }
        var second = rest.IndexOf(',');
        if (second < 0)
            return false;
        b = rest[..second];
        c = rest[(second + 1)..];
        return true;
    }

    private static async Task<string> CopyAssetAsync(string assetPath, string cacheDir, CancellationToken ct)
    {
        var name = Path.GetFileName(assetPath);
        var dest = Path.Combine(cacheDir, name);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            return dest;

        await using var src = await FileSystem.OpenAppPackageFileAsync(assetPath).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return dest;
    }
}
