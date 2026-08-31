using Microsoft.AspNetCore.Components;
using ProductSearch.Shared.Dtos;
using ProductSearch.UI.Services;

namespace ProductSearch.UI.Components.Shared;

public partial class ModelHud : IDisposable
{
    private const string DefaultModelId = "siglip-base-patch16-224";

    [Inject] private ApiClientService Api { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;

    private ModelBootstrapSnapshotDto? _boot;
    private string? _activeModelId;
    private bool _retrying;
    private bool _apiUnreachable;
    private bool _pollAfterFailure;
    private CancellationTokenSource? _pollCts;
    private PeriodicTimer? _timer;

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
        _pollCts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        _ = PollAsync(_pollCts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (_timer is null) return;
        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_boot?.State is "Ready" && !_apiUnreachable)
                    continue;

                if (_boot?.State is "Failed" && !_pollAfterFailure && !_retrying)
                    continue;

                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    if (_boot?.State is "Ready" or "Failed")
                        _pollAfterFailure = false;
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var status = await Api.GetStatusAsync();
            _boot = status?.ModelBootstrap;
            _activeModelId = status?.ActiveModelId;
            _apiUnreachable = false;
        }
        catch
        {
            _apiUnreachable = true;
            _boot = null;
        }
    }

    private string HudLabel() => _boot?.State switch
    {
        "Downloading" => "Downloading SigLIP",
        "Loading" => "Loading ONNX",
        "Checking" => "Checking models",
        _ => _boot?.Message ?? "Bootstrapping"
    };

    private async Task RetryAsync()
    {
        var modelId = string.IsNullOrWhiteSpace(_activeModelId) ? DefaultModelId : _activeModelId;
        _retrying = true;
        _pollAfterFailure = true;
        try
        {
            var result = await Api.SelectModelAsync(modelId);
            if (result?.Ok == true)
            {
                Toasts.Show("Model download restarted.", ToastLevel.Success);
                await RefreshAsync();
            }
            else
            {
                Toasts.Show(result?.Error ?? "Retry failed.", ToastLevel.Error);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        finally
        {
            _retrying = false;
        }
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _timer?.Dispose();
    }
}
