using Microsoft.AspNetCore.Diagnostics;

namespace ProductSearch.Api;

/// <summary>
/// Logs every unhandled request exception so it always appears in Aspire structured logs.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            exception,
            "Unhandled exception {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        if (httpContext.Response.HasStarted)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(
            new
            {
                error = exception.Message,
                detail = _env.IsDevelopment() ? exception.ToString() : null,
                path = httpContext.Request.Path.Value
            },
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}
