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
    private bool _initLoading = true;
    private bool _busy;
    private bool _ingestRunning;
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

    private string _queryModeLabel => HasImageQuery() ? "Image (Lens) query" : "Text query";

    private static bool IsHttpImageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https";
    }

    private bool HasImageQuery()
        => !string.IsNullOrEmpty(_imageBase64) || IsHttpImageUrl(_query);

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
        finally
        {
            try
            {
                await RefreshEncoderStateAsync();
            }
            finally
            {
                _initLoading = false;
            }
        }
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
            _ingestRunning = status?.IngestRunning == true;
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
            Toasts.Show("Enter a query, paste an image URL, or attach a Lens image.", ToastLevel.Warning);
            return;
        }

        _busy = true;
        _searchError = null;
        StateHasChanged();
        await Task.Yield();

        try
        {
            await RefreshEncoderStateAsync();
            _response = await Api.SearchAsync(BuildRequest());
            _searched = true;
        }
        catch (ApiException ex)
        {
            _searchError = ex.Message;
        }
        catch (Exception ex)
        {
            _searchError = ex.Message;
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private SearchRequestDto BuildRequest()
    {
        var request = new SearchRequestDto
        {
            Engine = _engine,
            Fusion = _fusion,
            TopK = _topK,
            UseInvertFilter = _useInvert,
            UseHybridFts = _useHybridFts,
            MasterCategory = _masterCategory
        };

        if (!string.IsNullOrEmpty(_imageBase64))
        {
            request.ImageBase64 = _imageBase64;
            request.QueryMode = QueryMode.Image;
            return request;
        }

        if (IsHttpImageUrl(_query))
        {
            request.ImageUrl = _query.Trim();
            request.QueryMode = QueryMode.Image;
            return request;
        }

        request.QueryText = _query;
        request.QueryMode = QueryMode.Text;
        return request;
    }

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

    private async Task CloseLens()
    {
        _lensOpen = false;
        _pendingImageBase64 = null;
        await RevokeLensPreviewUrlAsync(_lensPreviewUrl);
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
    public Task OnLensFileReceived(string base64, string contentType, string blobUrl)
        => SetPendingImageAsync(base64, contentType, blobUrl);

    [JSInvokable]
    public async Task OnLensUrlReceived(string url)
    {
        _query = url.Trim();
        _imageBase64 = null;
        _imagePreviewUrl = null;
        _pendingImageBase64 = null;
        await RevokeLensPreviewUrlAsync(_lensPreviewUrl);
        _lensPreviewUrl = null;
        _lensOpen = false;
        _dropRegistered = false;
        await SearchAsync();
    }

    private async Task SetPendingImageAsync(string base64, string contentType, string? blobUrl = null)
    {
        await RevokeLensPreviewUrlAsync(_lensPreviewUrl);
        _pendingImageBase64 = base64;
        _lensPreviewUrl = !string.IsNullOrEmpty(blobUrl)
            ? blobUrl
            : $"data:{contentType};base64,{base64}";
        await InvokeAsync(StateHasChanged);
    }

    private async Task RevokeLensPreviewUrlAsync(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("blob:", StringComparison.Ordinal))
            return;

        try
        {
            await Js.InvokeVoidAsync("productSearchLens.revokeBlobUrl", url);
        }
        catch
        {
            // JS may not be loaded yet during teardown.
        }
    }

    private async Task ConfirmLens()
    {
        if (string.IsNullOrEmpty(_pendingImageBase64))
            return;

        _imageBase64 = _pendingImageBase64;
        _imagePreviewUrl = _lensPreviewUrl;
        _query = "";
        _masterCategory = null;
        _pendingImageBase64 = null;
        _lensOpen = false;
        _dropRegistered = false;
        await SearchAsync();
    }

    private async Task ClearLens()
    {
        _imageBase64 = null;
        await RevokeLensPreviewUrlAsync(_imagePreviewUrl);
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
        var isImageQuery = HasImageQuery();
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
                __builder.OpenElement(22, "div");
                __builder.AddAttribute(23, "class", "product-tile__media");
                __builder.OpenComponent<ProductImage>(24);
                __builder.AddComponentParameter(25, nameof(ProductImage.Src), hit.Product.ImageUrl);
                __builder.AddComponentParameter(26, nameof(ProductImage.Alt), hit.Product.ProductDisplayName);
                __builder.AddComponentParameter(27, nameof(ProductImage.CssClass), "w-full object-cover");
                __builder.CloseComponent();
                __builder.OpenElement(28, "span");
                __builder.AddAttribute(29, "class", "product-match-badge");
                __builder.AddContent(30, $"{hit.SimilarityPercent:0.#}%");
                __builder.CloseElement();
                __builder.CloseElement();
                __builder.OpenElement(31, "div");
                __builder.AddAttribute(32, "class", "p-3 opacity-0 group-hover:opacity-100 transition-opacity");
                __builder.AddContent(33, hit.Product.ProductDisplayName);
                __builder.OpenElement(34, "div");
                __builder.AddAttribute(35, "class", "text-xs text-base-content/60");
                __builder.AddContent(36, $"#{hit.Rank}");
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
            __builder.OpenElement(112, "div");
            __builder.AddAttribute(113, "class", "product-card-media");
            __builder.OpenComponent<ProductImage>(114);
            __builder.AddComponentParameter(115, nameof(ProductImage.Src), hit.Product.ImageUrl);
            __builder.AddComponentParameter(116, nameof(ProductImage.Alt), hit.Product.ProductDisplayName);
            __builder.AddComponentParameter(117, nameof(ProductImage.CssClass), "w-24 h-32 object-cover rounded-xl");
            __builder.CloseComponent();
            __builder.OpenElement(118, "span");
            __builder.AddAttribute(119, "class", "product-match-badge");
            __builder.AddContent(120, $"{hit.SimilarityPercent:0.#}%");
            __builder.CloseElement();
            __builder.CloseElement();
            __builder.OpenElement(121, "div");
            __builder.AddAttribute(122, "class", "min-w-0 flex-1");
            __builder.OpenElement(123, "h3");
            __builder.AddAttribute(124, "class", "font-medium truncate");
            __builder.AddContent(125, hit.Product.ProductDisplayName);
            __builder.CloseElement();
            __builder.OpenElement(126, "p");
            __builder.AddAttribute(127, "class", "text-xs text-base-content/60 mt-1");
            __builder.AddContent(128, $"{hit.Product.BaseColour} · {hit.Product.Season} · {hit.Product.Usage}");
            __builder.CloseElement();
            __builder.OpenElement(129, "p");
            __builder.AddAttribute(130, "class", "text-xs text-primary mt-2");
            __builder.AddContent(131, $"#{hit.Rank} · {hit.Engine}");
            __builder.CloseElement();
            __builder.CloseElement();
            __builder.CloseElement();
        }
        __builder.CloseElement();
    };

    public async ValueTask DisposeAsync()
    {
        await RevokeLensPreviewUrlAsync(_lensPreviewUrl);
        await RevokeLensPreviewUrlAsync(_imagePreviewUrl);
        _dotNetRef?.Dispose();
    }
}
