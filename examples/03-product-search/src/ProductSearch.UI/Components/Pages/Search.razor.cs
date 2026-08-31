using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ProductSearch.Shared.Dtos;
using ProductSearch.Shared.Enums;
using ProductSearch.UI.Components.Shared;
using ProductSearch.UI.Services;

namespace ProductSearch.UI.Components.Pages;

public partial class Search : IAsyncDisposable
{
    private enum ResultsTab { All, Images }

    private sealed record TimingChip(string Label, string Value, string Tooltip);

    [Inject] private ApiClientService Api { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private string _query = "";
    private string? _imageBase64;
    private string? _imagePreviewUrl;
    private string? _pendingImageBase64;
    private string? _lensPreviewUrl;
    private VectorEngineMode _engine = VectorEngineMode.ZVec;
    private FusionMode _fusion = FusionMode.Rrf;
    private int _topK = 5;
    private bool _useInvert = false;
    private bool _useHybridFts = true;
    private string? _masterCategory;
    private bool _busy;
    private bool _searched;
    private string? _searchError;
    private bool _lensOpen;
    private bool _encoderReady;
    private ResultsTab _tab = ResultsTab.All;
    private SearchResponseDto? _response;
    private ModelBootstrapSnapshotDto? _boot;
    private List<WowQueryChipDto> _wowChips = [];
    private ElementReference _dropZone;
    private DotNetObjectReference<Search>? _dotNetRef;
    private bool _dropRegistered;

    private bool _hasResults => _response is not null &&
        ((_response.ZVecHits?.Count ?? 0) > 0 || (_response.PostgreSqlHits?.Count ?? 0) > 0);

    private string _queryModeLabel => string.IsNullOrEmpty(_imageBase64) ? "Text query" : "Image (Lens) query";

    protected override async Task OnInitializedAsync()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        try
        {
            _wowChips = (await Api.GetWowQueriesAsync())?.ToList() ?? [];
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastLevel.Warning);
        }

        await RefreshEncoderStateAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_lensOpen && !_dropRegistered)
        {
            await Js.InvokeVoidAsync("productSearchLens.registerDropZone", _dropZone, _dotNetRef);
            _dropRegistered = true;
        }
    }

    private async Task RefreshEncoderStateAsync()
    {
        try
        {
            var status = await Api.GetStatusAsync();
            _boot = status?.ModelBootstrap;
            _encoderReady = status?.ModelBootstrapComplete == true;
        }
        catch
        {
            _encoderReady = false;
        }
    }

    private async Task SearchAsync()
    {
        if (!_encoderReady)
        {
            Toasts.Show("SigLIP model is still loading.", ToastLevel.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_query) && string.IsNullOrEmpty(_imageBase64))
        {
            Toasts.Show("Enter a query or attach a Lens image.", ToastLevel.Warning);
            return;
        }

        _busy = true;
        _searchError = null;
        try
        {
            _response = await Api.SearchAsync(BuildRequest());
            _searched = true;
        }
        catch (ApiException ex)
        {
            _searchError = ex.Message;
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        catch (Exception ex)
        {
            _searchError = ex.Message;
            Toasts.Show(ex.Message, ToastLevel.Error);
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private SearchRequestDto BuildRequest() => new()
    {
        QueryText = _query,
        ImageBase64 = _imageBase64,
        QueryMode = string.IsNullOrEmpty(_imageBase64) ? QueryMode.Text : QueryMode.Image,
        Engine = _engine,
        Fusion = _fusion,
        TopK = _topK,
        UseInvertFilter = _useInvert,
        UseHybridFts = _useHybridFts,
        MasterCategory = _masterCategory
    };

    private async Task ApplyChip(WowQueryChipDto chip)
    {
        _query = chip.QueryText;
        _imageBase64 = null;
        _imagePreviewUrl = null;
        _masterCategory = chip.MasterCategory;
        await SearchAsync();
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await SearchAsync();
    }

    private void OpenLens()
    {
        _pendingImageBase64 = _imageBase64;
        _lensPreviewUrl = _imagePreviewUrl;
        _lensOpen = true;
        _dropRegistered = false;
    }

    private void CloseLens()
    {
        _lensOpen = false;
        _pendingImageBase64 = null;
        _lensPreviewUrl = null;
        _dropRegistered = false;
    }

    private async Task OpenFilePicker()
        => await Js.InvokeVoidAsync("eval", "document.getElementById('lens-file-input')?.click()");

    private async Task OnLensFileSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null) return;
        await using var stream = file.OpenReadStream(8 * 1024 * 1024);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        await SetPendingImageAsync(Convert.ToBase64String(ms.ToArray()), file.ContentType);
    }

    [JSInvokable]
    public Task OnLensFileReceived(string base64, string contentType)
        => SetPendingImageAsync(base64, contentType);

    private Task SetPendingImageAsync(string base64, string contentType)
    {
        _pendingImageBase64 = base64;
        _lensPreviewUrl = $"data:{contentType};base64,{base64}";
        return InvokeAsync(StateHasChanged);
    }

    private async Task ConfirmLens()
    {
        if (string.IsNullOrEmpty(_pendingImageBase64))
            return;

        _imageBase64 = _pendingImageBase64;
        _imagePreviewUrl = _lensPreviewUrl;
        _query = "";
        _masterCategory = null;
        CloseLens();
        await SearchAsync();
    }

    private void ClearLens()
    {
        _imageBase64 = null;
        _imagePreviewUrl = null;
    }

    private static string FormatMs(double ms) => ms.ToString("0.#");

    private string FormatStage(double ms, bool used)
        => used ? FormatMs(ms) : "—";

    private IEnumerable<TimingChip> BuildTimingChips()
    {
        if (_response?.Latency is null)
            yield break;

        var lat = _response.Latency;
        var isImageQuery = !string.IsNullOrEmpty(_imageBase64);
        var usesZvec = _engine is VectorEngineMode.ZVec or VectorEngineMode.Both;
        var usesPg = _engine is VectorEngineMode.Postgres or VectorEngineMode.Both;
        var usesFts = usesZvec && _useHybridFts && !isImageQuery;

        yield return new TimingChip("Encode", FormatMs(lat.EncodeMs),
            "SigLIP turns the query (text or photo) into a vector. Usually the largest number.");
        yield return new TimingChip("Text ANN", FormatStage(lat.TextAnnMs, usesZvec && !isImageQuery),
            "ZVec nearest-neighbor on product text embeddings.");
        yield return new TimingChip("Image ANN", FormatStage(lat.ImageAnnMs, usesZvec && isImageQuery),
            "ZVec nearest-neighbor on product photos.");
        yield return new TimingChip("FTS", FormatStage(lat.FtsMs, usesFts),
            "Keyword full-text on product descriptions (Hybrid FTS).");
        yield return new TimingChip("Fuse", FormatStage(lat.FuseMs, usesZvec && !isImageQuery && _engine == VectorEngineMode.ZVec),
            "Merge text + image (and FTS) ranked lists.");
        yield return new TimingChip("pgvector", FormatStage(lat.PgVectorMs, usesPg),
            "Postgres cosine search (0 when Engine is ZVec-only).");
        yield return new TimingChip("SQL", FormatMs(lat.SqlHydrateMs),
            "Load product cards from Postgres after ANN (hydrate).");
    }

    private RenderFragment RenderResultList(IReadOnlyList<SearchHitDto>? hits) => __builder =>
    {
        if (hits is null || hits.Count == 0)
        {
            __builder.OpenElement(0, "p");
            __builder.AddAttribute(1, "class", "text-sm text-base-content/50");
            __builder.AddContent(2, "No results.");
            __builder.CloseElement();
            return;
        }

        if (_tab == ResultsTab.Images)
        {
            __builder.OpenElement(10, "div");
            __builder.AddAttribute(11, "class", "product-grid--masonry");
            foreach (var hit in hits)
            {
                __builder.OpenElement(20, "article");
                __builder.AddAttribute(21, "class", "product-tile group");
                __builder.OpenComponent<ProductImage>(22);
                __builder.AddComponentParameter(23, nameof(ProductImage.Src), hit.Product.ImageUrl);
                __builder.AddComponentParameter(24, nameof(ProductImage.Alt), hit.Product.ProductDisplayName);
                __builder.AddComponentParameter(25, nameof(ProductImage.CssClass), "w-full object-cover");
                __builder.CloseComponent();
                __builder.OpenElement(26, "div");
                __builder.AddAttribute(27, "class", "p-3 opacity-0 group-hover:opacity-100 transition-opacity");
                __builder.AddContent(28, hit.Product.ProductDisplayName);
                __builder.OpenElement(29, "div");
                __builder.AddAttribute(30, "class", "text-xs text-base-content/60");
                __builder.AddContent(31, $"#{hit.Rank} · {hit.SimilarityPercent:0.#}%");
                __builder.CloseElement();
                __builder.CloseElement();
                __builder.CloseElement();
            }
            __builder.CloseElement();
            return;
        }

        __builder.OpenElement(100, "div");
        __builder.AddAttribute(101, "class", "flex flex-col gap-3");
        foreach (var hit in hits)
        {
            __builder.OpenElement(110, "article");
            __builder.AddAttribute(111, "class", "product-card-row");
            __builder.OpenComponent<ProductImage>(112);
            __builder.AddComponentParameter(113, nameof(ProductImage.Src), hit.Product.ImageUrl);
            __builder.AddComponentParameter(114, nameof(ProductImage.Alt), hit.Product.ProductDisplayName);
            __builder.AddComponentParameter(115, nameof(ProductImage.CssClass), "w-24 h-32 object-cover rounded-xl shrink-0");
            __builder.CloseComponent();
            __builder.OpenElement(117, "div");
            __builder.AddAttribute(118, "class", "min-w-0 flex-1");
            __builder.OpenElement(119, "h3");
            __builder.AddAttribute(120, "class", "font-medium truncate");
            __builder.AddContent(121, hit.Product.ProductDisplayName);
            __builder.CloseElement();
            __builder.OpenElement(122, "p");
            __builder.AddAttribute(123, "class", "text-xs text-base-content/60 mt-1");
            __builder.AddContent(124, $"{hit.Product.BaseColour} · {hit.Product.Season} · {hit.Product.Usage}");
            __builder.CloseElement();
            __builder.OpenElement(125, "p");
            __builder.AddAttribute(126, "class", "text-xs text-primary mt-2");
            __builder.AddContent(127, $"#{hit.Rank} · {hit.SimilarityPercent:0.#}% · {hit.Engine}");
            __builder.CloseElement();
            __builder.CloseElement();
            __builder.CloseElement();
        }
        __builder.CloseElement();
    };

    public async ValueTask DisposeAsync()
    {
        _dotNetRef?.Dispose();
        await ValueTask.CompletedTask;
    }
}
