using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class ZLensBrand
{
    [Parameter] public bool ShowBadge { get; set; } = true;
    [Parameter] public string? CssClass { get; set; }
}
