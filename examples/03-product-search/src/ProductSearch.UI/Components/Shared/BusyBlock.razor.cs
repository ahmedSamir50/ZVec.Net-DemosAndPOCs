using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class BusyBlock
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public string Message { get; set; } = "Loading…";
    [Parameter] public string? SubMessage { get; set; }
    [Parameter] public string? CssClass { get; set; }
}
