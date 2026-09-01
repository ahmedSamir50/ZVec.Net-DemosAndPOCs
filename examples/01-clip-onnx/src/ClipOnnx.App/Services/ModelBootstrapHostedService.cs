using ClipOnnx.App.DataModels;
using ClipOnnx.App.Options;
using Microsoft.Extensions.Options;

namespace ClipOnnx.App.Services;

/// <summary>
/// Startup glue: load the configured <see cref="ClipOnnxOptions.ActiveModelId"/>
/// (download ONNX if needed), then initialize the encoder. Model switches after startup
/// go through <see cref="IClipModelSelectionService.SelectAsync"/>.
/// </summary>
public sealed class ModelBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ModelBootstrapStatus _status;
    private readonly IOptions<ClipOnnxOptions> _options;
    private readonly ILogger<ModelBootstrapHostedService> _logger;
    private Task? _work;

    public ModelBootstrapHostedService(
        IServiceProvider services,
        ModelBootstrapStatus status,
        IOptions<ClipOnnxOptions> options,
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
        try { await _work.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var selector = _services.GetRequiredService<IClipModelSelectionService>();
            var id = string.IsNullOrWhiteSpace(_options.Value.ActiveModelId)
                ? ClipModelCatalog.DefaultModelId
                : _options.Value.ActiveModelId;
            var result = await selector.SelectAsync(id, ct).ConfigureAwait(false);
            if (!result.Ok)
                throw new InvalidOperationException(result.Error ?? "Model bootstrap failed.");
            _logger.LogInformation("Bootstrapped CLIP model {ModelId}", result.ActiveModelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model bootstrap failed");
            _status.SetState(ModelBootstrapState.Failed, "Model bootstrap failed", ex.Message);
        }
    }
}
