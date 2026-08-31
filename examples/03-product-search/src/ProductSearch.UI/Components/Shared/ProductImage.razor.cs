using Microsoft.AspNetCore.Components;
using ProductSearch.Shared.Constants;

namespace ProductSearch.UI.Components.Shared;

public partial class ProductImage
{
    [Parameter, EditorRequired] public string? Src { get; set; }
    [Parameter] public string Alt { get; set; } = "";
    [Parameter] public string CssClass { get; set; } = "";

    [Inject] private IConfiguration Config { get; set; } = default!;

    private string ResolvedSrc { get; set; } = "";
    private bool _usePlaceholder;

    private string ApiBaseUrl =>
        (Config[$"{ConfigurationSections.ProductSearchUi}:ApiBaseUrl"] ?? "http://localhost:5110").TrimEnd('/');

    protected override void OnParametersSet()
    {
        if (_usePlaceholder)
        {
            ResolvedSrc = PlaceholderDataUri;
            return;
        }

        ResolvedSrc = Resolve(Src);
    }

    private string Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return PlaceholderDataUri;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return url;

        return $"{ApiBaseUrl}{url}";
    }

    private void OnImageError()
    {
        if (_usePlaceholder)
            return;

        _usePlaceholder = true;
        ResolvedSrc = PlaceholderDataUri;
    }

    private const string PlaceholderDataUri =
        "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='120' height='160' viewBox='0 0 120 160'%3E%3Crect fill='%23222a3d' width='120' height='160' rx='12'/%3E%3Cpath fill='%23958da2' d='M36 58h48v8H36zm0 20h48v8H36zm0 20h32v8H36z'/%3E%3C/svg%3E";
}
