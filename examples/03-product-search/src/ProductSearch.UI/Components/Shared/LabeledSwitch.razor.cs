using Microsoft.AspNetCore.Components;

namespace ProductSearch.UI.Components.Shared;

public partial class LabeledSwitch
{
    [Parameter] public string Label { get; set; } = "";
    [Parameter] public string? Description { get; set; }
    [Parameter] public bool Value { get; set; }
    [Parameter] public EventCallback<bool> ValueChanged { get; set; }
    [Parameter] public bool Disabled { get; set; }

    private async Task ToggleAsync()
    {
        if (Disabled)
            return;

        Value = !Value;
        await ValueChanged.InvokeAsync(Value);
    }
}
