using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ProductSearch.Shared.Dtos;
using ProductSearch.UI.Services;

namespace ProductSearch.UI.Components.Pages;

public partial class Ingest : IDisposable
{
    [Inject] private ApiClientService Api { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private IngestProgressDto? _progress;
    private bool _busy;
    private int _patchSize = 100;
    private bool _optimizeAfterPatch = true;
    private string? _loadError;
    private CancellationTokenSource? _pollCts;
    private ElementReference _terminal;

    private string ActiveMessage =>
        string.IsNullOrWhiteSpace(_progress?.Message)
            ? "Ingest running…"
            : _progress!.Message;

    private double? ProgressPercent
    {
        get
        {
            if (_progress is null || !_progress.IsRunning)
                return null;

            if (_progress.Status is "Encoding" && _progress.PatchSize > 0)
                return Math.Clamp(_progress.Encoded * 100.0 / _progress.PatchSize, 0, 100);

            if (_progress.Status is "Downloading"
                && _progress.DownloadBytesTotal is > 0)
            {
                return Math.Clamp(
                    _progress.DownloadBytesReceived * 100.0 / _progress.DownloadBytesTotal.Value,
                    0,
                    100);
            }

            return null;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await ReloadProgressAsync();
        if (_progress?.IsRunning == true)
        {
            BeginPolling();
            _ = ResumePollingAsync();
        }
    }

    private async Task ReloadProgressAsync()
    {
        _loadError = null;
        try
        {
            _progress = await Api.GetIngestAsync();
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            Toasts.Show(ex.Message, ToastLevel.Warning);
        }
    }

    private async Task ResumePollingAsync()
    {
        try
        {
            await PollUntilDoneAsync();
        }
        catch
        {
            // ignore
        }
        finally
        {
            StopPolling();
            _busy = false;
            try { _progress = await Api.GetIngestAsync() ?? _progress; }
            catch { /* ignore */ }
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task RunAsync()
    {
        _busy = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            _progress = await Api.StartIngestAsync(new IngestRequestDto
            {
                PatchSize = _patchSize,
                OptimizeAfterPatch = _optimizeAfterPatch
            });

            if (_progress is null)
                return;

            if (!_progress.IsRunning && !string.IsNullOrEmpty(_progress.ErrorMessage))
            {
                Toasts.Show(_progress.ErrorMessage, ToastLevel.Error);
                return;
            }

            BeginPolling();
            await PollUntilDoneAsync();
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        finally
        {
            StopPolling();
            _busy = false;
            try { _progress = await Api.GetIngestAsync() ?? _progress; }
            catch { /* ignore */ }
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OptimizeAsync()
    {
        _busy = true;
        try
        {
            await Api.OptimizeIndexAsync();
            Toasts.Show("Optimized — flat buffers merged into HNSW.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void BeginPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _busy = true;
    }

    private void StopPolling()
    {
        if (_pollCts is null)
            return;

        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = null;
    }

    private async Task PollUntilDoneAsync()
    {
        var ct = _pollCts?.Token ?? CancellationToken.None;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var p = await Api.GetIngestAsync(ct);
                if (p is not null)
                {
                    _progress = p;
                    await InvokeAsync(StateHasChanged);
                    await ScrollTerminalAsync();

                    if (!p.IsRunning)
                    {
                        if (p.Status is "Failed")
                            Toasts.Show(p.ErrorMessage ?? "Ingest failed", ToastLevel.Error);
                        else if (p.Status is "Completed")
                            Toasts.Show(p.Message, ToastLevel.Success);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // transient poll errors
            }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private string FormatOffset()
    {
        if (_progress is null)
            return "—";
        if (_progress.CatalogTotal > 0)
            return $"{_progress.IngestOffset} / {_progress.CatalogTotal}";
        return _progress.IngestOffset.ToString();
    }

    private string FormatCatalogTotal()
        => _progress?.CatalogTotal > 0 ? _progress.CatalogTotal.ToString() : "?";

    private async Task ScrollTerminalAsync()
    {
        if (_progress?.Events.Count is not > 0)
            return;

        try
        {
            await Js.InvokeVoidAsync("productSearchIngestShader.scrollTerminal", _terminal);
        }
        catch
        {
            // ignore scroll errors during teardown
        }
    }

    public void Dispose()
    {
        StopPolling();
    }
}
