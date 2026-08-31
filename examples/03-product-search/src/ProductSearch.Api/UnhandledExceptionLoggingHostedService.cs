namespace ProductSearch.Api;

/// <summary>Catches process-wide failures that never hit the ASP.NET pipeline (background tasks, finalizers).</summary>
public sealed class UnhandledExceptionLoggingHostedService : IHostedService
{
    private readonly ILogger<UnhandledExceptionLoggingHostedService> _logger;

    public UnhandledExceptionLoggingHostedService(ILogger<UnhandledExceptionLoggingHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        return Task.CompletedTask;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger.LogCritical(ex, "AppDomain unhandled exception (isTerminating={Terminating})", e.IsTerminating);
        else
            _logger.LogCritical("AppDomain unhandled exception: {Object}", e.ExceptionObject);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
