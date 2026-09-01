using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;
using ProductSearch.Core.Storage;

namespace ProductSearch.Core.Services;

public sealed class ModelBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ProductSearchOptions> _options;
    private readonly ILogger<ModelBootstrapHostedService> _logger;
    private CancellationTokenSource? _workCts;
    private Task? _work;

    public ModelBootstrapHostedService(
        IServiceProvider services,
        IOptions<ProductSearchOptions> options,
        ILogger<ModelBootstrapHostedService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _work = Task.Run(() => RunAsync(_workCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Do not block shutdown on model download — ZVec collections must close promptly.
        _workCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var selector = _services.GetRequiredService<ISigLipModelSelectionService>();
            var stampStore = _services.GetRequiredService<IIndexStampStore>();
            var stamp = stampStore.Load();
            var id = !string.IsNullOrWhiteSpace(stamp.ModelId)
                ? stamp.ModelId
                : (string.IsNullOrWhiteSpace(_options.Value.ActiveModelId)
                    ? SigLipModelCatalog.DefaultModelId
                    : _options.Value.ActiveModelId);
            var result = await selector.SelectAsync(id, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogError("Model bootstrap failed: {Error}", result.Error);
                return;
            }

            _logger.LogInformation("Bootstrapped SigLIP model {ModelId}", result.ActiveModelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Model bootstrap cancelled during host shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model bootstrap failed");
            var (summary, detail) = BootstrapExceptionFormatter.Format(ex);
            var bootstrap = _services.GetRequiredService<ModelBootstrapStatus>();
            bootstrap.SetFailure("Model bootstrap failed", summary, detail);
        }
    }
}
