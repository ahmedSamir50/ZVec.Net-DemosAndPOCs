using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class EmptyState
{
    [Parameter] public string Icon { get; set; } = "🔍";
    [Parameter] public string Title { get; set; } = "Nothing here yet";
    [Parameter] public string? Message { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? CssClass { get; set; }
}
