using Microsoft.AspNetCore.Components;
using ProductSearch.UI.Services;

namespace ProductSearch.UI.Components.Layout;

public partial class MainLayout
{
    [Inject] private ApiClientService Api { get; set; } = default!;

    private bool _demoReady;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var status = await Api.GetStatusAsync();
            _demoReady = status?.DemoReady == true;
        }
        catch
        {
            _demoReady = false;
        }
    }
}
