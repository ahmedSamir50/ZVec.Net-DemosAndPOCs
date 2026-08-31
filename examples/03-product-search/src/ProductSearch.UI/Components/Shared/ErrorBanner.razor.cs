using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class ErrorBanner
{
    [Parameter] public string Title { get; set; } = "Something went wrong";
    [Parameter] public string? Message { get; set; }
    [Parameter] public string RetryLabel { get; set; } = "Retry";
    [Parameter] public bool RetryDisabled { get; set; }
    [Parameter] public EventCallback OnRetry { get; set; }
    [Parameter] public string? CssClass { get; set; }
}
