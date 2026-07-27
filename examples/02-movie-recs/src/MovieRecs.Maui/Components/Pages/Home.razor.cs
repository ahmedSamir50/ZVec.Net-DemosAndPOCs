using Microsoft.AspNetCore.Components;
using MovieRecs.Maui.Models;
using MovieRecs.Maui.Services;
using MudBlazor;
using Color = MudBlazor.Color;

namespace MovieRecs.Maui.Components.Pages;

/// <summary>
/// Edge demo UI: Ingest → Optimize (auto + button), watchlist behaviour vector → Recommend.
/// User/seed changes clear <c>_hits</c> so stale rec tiles never look unchanged.
/// Perf strip polls <see cref="PerfMonitorService"/> ~1s (CPU delta + working set).
/// </summary>
public partial class Home : IDisposable
{
    private const int MaxVisibleChips = 6;
    private const int MaxUserLikes = 12;

    [Inject] private IMovieLensCatalog Catalog { get; set; } = default!;
    [Inject] private IMovieLensIngestService Ingest { get; set; } = default!;
    [Inject] private IRecommendService Recommend { get; set; } = default!;
    [Inject] private IIndexStampStore Stamp { get; set; } = default!;
    [Inject] private IngestProgressStatus Progress { get; set; } = default!;
    [Inject] private PerfMonitorService Perf { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly string[] _seeds = ["Toy Story", "The Matrix", "Spirited Away", "Inception"];
    private readonly string[] _genres = ["Action", "Adventure", "Animation", "Comedy", "Drama", "Horror", "Romance", "Sci-Fi", "Thriller"];

    private readonly List<MovieInfo> _watchlist = [];
    private List<UserSummary> _users = [];
    private List<RecommendHit> _hits = [];
    private MovieInfo? _pick;
    private int? _userId;
    private string? _genre;
    private int _topK = 12;
    private bool _busy;
    private string _statusLabel = "Not indexed";
    private int _indexedCount;
    private IngestSnapshot _snap = new(false, 0, 0, "Idle", null, 0);
    private PerfSnapshot _perf = new(0, 0, 0);
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _perfCts;

    private bool CanRecommend => !_busy && _watchlist.Count > 0 && _indexedCount > 0;
    private Color StatusColor => _indexedCount > 0 ? Color.Success : Color.Default;
    private IEnumerable<MovieInfo> VisibleWatchlist => _watchlist.Take(MaxVisibleChips);

    private string _watchlistSummary =>
        _userId is int uid
            ? $"Watchlist · {_watchlist.Count} titles · User {uid}"
            : $"Watchlist · {_watchlist.Count} titles";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await Catalog.EnsureLoadedAsync();
            _users = Catalog.Users.ToList();
            RefreshStamp();
            _perf = Perf.Sample();
            _perfCts = new CancellationTokenSource();
            _ = PollPerfAsync(_perfCts.Token);
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    private void RefreshStamp()
    {
        var stamp = Stamp.Load();
        _indexedCount = Stamp.IsReady(stamp) ? stamp.Count : 0;
        _statusLabel = _indexedCount > 0 ? "Demo ready" : Stamp.IsMismatch(stamp) ? "Index mismatch — reset" : "Not indexed";
        _snap = Progress.Snapshot();
    }

    /// <summary>Clear recommendations so user/watchlist changes are visible without reusing stale tiles.</summary>
    private void InvalidateHits() => _hits.Clear();

    private async Task RunIngestAsync()
    {
        _busy = true;
        _pollCts = new CancellationTokenSource();
        _ = PollProgressAsync(_pollCts.Token);
        try
        {
            var ok = await Ingest.IngestAsync();
            RefreshStamp();
            Snackbar.Add(ok ? "Ingest complete (index optimized)." : "Ingest failed.", ok ? Severity.Success : Severity.Error);
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            RefreshStamp();
        }
        finally
        {
            _pollCts.Cancel();
            _busy = false;
            _snap = Progress.Snapshot();
        }
    }

    private async Task RunOptimizeAsync()
    {
        _busy = true;
        try
        {
            // Opens collection if needed; merges flat upsert buffer into HNSW (no re-embed).
            await Ingest.OptimizeAsync();
            Snackbar.Add("Optimized — flat upsert buffer merged into HNSW.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PollProgressAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _snap = Progress.Snapshot();
                await InvokeAsync(StateHasChanged);
                await Task.Delay(400, ct);
            }
        }
        catch (OperationCanceledException) { /* done */ }
    }

    private async Task PollPerfAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _perf = Perf.Sample();
                await InvokeAsync(StateHasChanged);
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    private async Task RunResetAsync()
    {
        _busy = true;
        try
        {
            await Ingest.ResetAsync();
            InvalidateHits();
            RefreshStamp();
            Snackbar.Add("Index reset.", Severity.Info);
        }
        finally
        {
            _busy = false;
        }
    }

    private Task<IEnumerable<MovieInfo>> SearchMovies(string value, CancellationToken ct) =>
        Task.FromResult(Catalog.Search(value, 25).AsEnumerable());

    private Task OnPickChanged(MovieInfo? m)
    {
        _pick = null;
        if (m is not null)
        {
            AddToWatchlist(m);
            InvalidateHits();
        }
        return Task.CompletedTask;
    }

    private Task AddSeedAsync(string fragment)
    {
        var m = Catalog.FindByTitleContains(fragment);
        if (m is null)
            Snackbar.Add($"No match for “{fragment}”.", Severity.Warning);
        else
        {
            AddToWatchlist(m);
            InvalidateHits();
        }
        return Task.CompletedTask;
    }

    private async Task LoadUser1Async() => await OnUserChanged(1);

    private Task OnUserChanged(int? userId)
    {
        _userId = userId;
        if (userId is null)
            return Task.CompletedTask;

        // Replace watchlist with this user's likes (not merge) — demo “behaviour as a vector”.
        _watchlist.Clear();
        InvalidateHits();
        foreach (var m in Catalog.LikesForUser(userId.Value).Take(MaxUserLikes))
            AddToWatchlist(m);
        Snackbar.Add($"Loaded {_watchlist.Count} likes for User {userId.Value}.", Severity.Info);
        return Task.CompletedTask;
    }

    private void AddToWatchlist(MovieInfo m)
    {
        if (_watchlist.Any(x => x.Id == m.Id))
            return;
        _watchlist.Add(m);
    }

    private void RemoveWatch(MudChip<MovieInfo> chip)
    {
        if (chip.Value is not null)
        {
            _watchlist.RemoveAll(m => m.Id == chip.Value.Id);
            InvalidateHits();
        }
    }

    private async Task RunRecommendAsync()
    {
        _busy = true;
        try
        {
            _hits = (await Recommend.RecommendAsync(
                _watchlist.Select(m => m.Id).ToList(),
                _topK,
                _genre)).ToList();
            if (_hits.Count == 0)
                Snackbar.Add("No recommendations — try ingest, Optimize, or a different watchlist.", Severity.Warning);
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task MoreLikeThisAsync(RecommendHit hit)
    {
        _watchlist.Clear();
        _userId = null;
        InvalidateHits();
        if (Catalog.ById.TryGetValue(hit.Id, out var info))
            AddToWatchlist(info);
        await RunRecommendAsync();
    }

    private static string ShortTitle(string title) =>
        title.Length <= 28 ? title : title[..25] + "…";

    private static string FormatGenres(string genres) =>
        genres.Replace("|", " · ", StringComparison.Ordinal);

    private static string GenreColor(string genres)
    {
        var g = genres.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Drama";
        return g switch
        {
            "Action" => "#3A1C1C",
            "Adventure" => "#1C2E3A",
            "Animation" => "#2A1C3A",
            "Comedy" => "#3A321C",
            "Drama" => "#1C1C2E",
            "Horror" => "#1A0F14",
            "Romance" => "#3A1C28",
            "Sci-Fi" => "#14283A",
            "Thriller" => "#241C1C",
            _ => "#1E1E26"
        };
    }

    public void Dispose()
    {
        _perfCts?.Cancel();
        _perfCts?.Dispose();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
    }
}
