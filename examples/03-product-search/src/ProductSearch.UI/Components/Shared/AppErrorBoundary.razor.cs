using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class AppErrorBoundary
{
    [Inject]
    private ILogger<AppErrorBoundary> Logger { get; set; } = default!;

    protected override Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "Unhandled Blazor UI exception");
        return Task.CompletedTask;
    }

    private void RecoverClicked() => Recover();
}
