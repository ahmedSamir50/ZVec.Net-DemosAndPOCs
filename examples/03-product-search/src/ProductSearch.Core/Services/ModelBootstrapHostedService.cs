using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;

namespace ProductSearch.Core.Services;

public sealed class ModelBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ModelBootstrapStatus _status;
    private readonly IOptions<ProductSearchOptions> _options;
    private readonly ILogger<ModelBootstrapHostedService> _logger;
    private Task? _work;

    public ModelBootstrapHostedService(
        IServiceProvider services,
        ModelBootstrapStatus status,
        IOptions<ProductSearchOptions> options,
        ILogger<ModelBootstrapHostedService> logger)
    {
        _services = services;
        _status = status;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _work = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_work is null) return;
        try { await _work.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var selector = _services.GetRequiredService<ISigLipModelSelectionService>();
            var id = string.IsNullOrWhiteSpace(_options.Value.ActiveModelId)
                ? SigLipModelCatalog.DefaultModelId
                : _options.Value.ActiveModelId;
            var result = await selector.SelectAsync(id, ct).ConfigureAwait(false);
            if (!result.Ok)
                throw new InvalidOperationException(result.Error ?? "Model bootstrap failed.");
            _logger.LogInformation("Bootstrapped SigLIP model {ModelId}", result.ActiveModelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model bootstrap failed");
            _status.SetState(ModelBootstrapState.Failed, "Model bootstrap failed", ex.Message);
        }
    }
}
