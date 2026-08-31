using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class ZLensBrand
{
    [Parameter] public bool ShowBadge { get; set; } = true;
    [Parameter] public bool Animate { get; set; }
    [Parameter] public string? CssClass { get; set; }

    private string GradientId { get; } = "zlens-bg-" + Guid.NewGuid().ToString("N")[..8];
}
