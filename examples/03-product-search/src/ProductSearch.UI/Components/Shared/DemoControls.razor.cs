using Microsoft.AspNetCore.Components;
using ProductSearch.Shared.Enums;

namespace ProductSearch.UI.Components.Shared;

public partial class DemoControls
{
    [Parameter] public VectorEngineMode Engine { get; set; }
    [Parameter] public EventCallback<VectorEngineMode> EngineChanged { get; set; }

    [Parameter] public int TopK { get; set; }
    [Parameter] public EventCallback<int> TopKChanged { get; set; }

    [Parameter] public FusionMode Fusion { get; set; }
    [Parameter] public EventCallback<FusionMode> FusionChanged { get; set; }

    [Parameter] public string? MasterCategory { get; set; }
    [Parameter] public EventCallback<string?> MasterCategoryChanged { get; set; }

    [Parameter] public bool UseInvert { get; set; }
    [Parameter] public EventCallback<bool> UseInvertChanged { get; set; }

    [Parameter] public bool UseHybridFts { get; set; }
    [Parameter] public EventCallback<bool> UseHybridFtsChanged { get; set; }

    [Parameter] public bool ShowFusion { get; set; }

    protected static readonly string[] Categories =
    [
        "Apparel", "Footwear", "Accessories", "Personal Care", "Sporting Goods", "Home"
    ];
}
