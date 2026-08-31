using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductSearch.Core.Services;
using ProductSearch.Core.Storage;
using ZVec.NET;

namespace ProductSearch.Core.DependencyInjection;

/// <summary>
/// On host shutdown: cancel ingest, drain in-flight ZVec ops, close collections via SDK Dispose
/// (SafeZvecHandle releases RocksDB LOCK). <see cref="IZvecFactory.Shutdown"/> is invoked by AddZVec DI teardown.
/// </summary>
internal sealed class ZVecShutdownHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DualCollectionHolder _collections;
    private readonly IIngestService _ingest;
    private readonly ILogger<ZVecShutdownHostedService> _logger;

    public ZVecShutdownHostedService(
        IHostApplicationLifetime lifetime,
        DualCollectionHolder collections,
        IIngestService ingest,
        ILogger<ZVecShutdownHostedService> logger)
    {
        _lifetime = lifetime;
        _collections = collections;
        _ingest = ingest;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnStopping()
    {
        try
        {
            _logger.LogInformation("Application stopping — cancelling ingest and closing ZVec collections");
            _ingest.CancelRunningPatch();
            _collections.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during ZVec shutdown");
        }
    }
}
