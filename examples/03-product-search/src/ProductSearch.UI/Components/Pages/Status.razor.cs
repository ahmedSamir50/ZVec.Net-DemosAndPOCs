using Microsoft.AspNetCore.Components;
using ProductSearch.Shared.Dtos;
using ProductSearch.UI.Services;

namespace ProductSearch.UI.Components.Pages;

public partial class Status
{
    [Inject] private ApiClientService Api { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;

    private StatusDto? _status;
    private ModelsResponseDto? _models;
    private bool _loading;
    private bool _busy;
    private bool _retryBusy;
    private string? _loadError;

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _loading = true;
        _loadError = null;
        try
        {
            _status = await Api.GetStatusAsync();
            _models = await Api.GetModelsAsync();
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RetryModelAsync()
    {
        var modelId = string.IsNullOrWhiteSpace(_status?.ActiveModelId)
            ? "siglip-base-patch16-224"
            : _status.ActiveModelId;

        _retryBusy = true;
        try
        {
            var result = await Api.SelectModelAsync(modelId);
            if (result?.Ok == true)
                Toasts.Show("Model download restarted.", ToastLevel.Success);
            else
                Toasts.Show(result?.Error ?? "Retry failed.", ToastLevel.Error);

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        finally
        {
            _retryBusy = false;
        }
    }

    private async Task OnModelChanged(ChangeEventArgs e)
    {
        var modelId = e.Value?.ToString();
        if (string.IsNullOrWhiteSpace(modelId))
            return;

        _busy = true;
        try
        {
            var result = await Api.SelectModelAsync(modelId);
            if (result?.Ok == true)
            {
                Toasts.Show($"Switched to {modelId}", ToastLevel.Success);
                await RefreshAsync();
            }
            else
            {
                Toasts.Show(result?.Error ?? "Model switch failed", ToastLevel.Error);
            }
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

    private async Task OptimizeAsync()
    {
        _busy = true;
        try
        {
            await Api.OptimizeIndexAsync();
            Toasts.Show("Indexes optimized.", ToastLevel.Success);
            await RefreshAsync();
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

    private async Task ResetIndexesAsync()
    {
        _busy = true;
        try
        {
            await Api.ResetIndexesAsync();
            Toasts.Show("ZVec indexes reset.", ToastLevel.Warning);
            await RefreshAsync();
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

    private async Task ResetCatalogAsync()
    {
        _busy = true;
        try
        {
            await Api.ResetCatalogAsync();
            Toasts.Show("Catalog and ZVec indexes cleared.", ToastLevel.Warning);
            await RefreshAsync();
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

    private static string BootstrapBadgeClass(string state) => state switch
    {
        "Ready" => "badge-success",
        "Failed" => "badge-error",
        "Downloading" => "badge-info",
        _ => "badge-ghost"
    };

    private static string FormatBytes(long received, long? total)
    {
        if (total is > 0)
            return $"{FormatSize(received)} / {FormatSize(total.Value)}";

        return FormatSize(received);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824)
            return $"{bytes / 1_073_741_824.0:0.##} GB";
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576.0:0.##} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0.##} KB";
        return $"{bytes} B";
    }
}
