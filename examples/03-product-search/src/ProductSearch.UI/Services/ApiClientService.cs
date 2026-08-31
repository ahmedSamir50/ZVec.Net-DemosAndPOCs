using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProductSearch.Shared.Constants;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.UI.Services;

/// <summary>HTTP client for ProductSearch.Api REST endpoints.</summary>
public sealed class ApiClientService
{
    private readonly HttpClient _http;
    private readonly ILogger<ApiClientService> _logger;

    public ApiClientService(IHttpClientFactory httpClientFactory, ILogger<ApiClientService> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientNames.ProductSearchApi);
        _logger = logger;
    }

    public async Task<SearchResponseDto?> SearchAsync(SearchRequestDto request, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var response = await _http.PostAsJsonAsync(ApiRoutes.Search.TrimStart('/'), request, ApiJson.Options, timeoutCts.Token)
                .ConfigureAwait(false);
            return await ReadSuccessAsync<SearchResponseDto>(response, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(System.Net.HttpStatusCode.RequestTimeout, "Search timed out after 30 seconds.");
        }
    }

    public async Task<SearchResponseDto?> SimilarAsync(Guid productId, SearchRequestDto request, CancellationToken ct = default)
    {
        var route = $"{ApiRoutes.Search}/similar/{productId}".TrimStart('/');
        var response = await _http.PostAsJsonAsync(route, request, ApiJson.Options, ct).ConfigureAwait(false);
        return await ReadSuccessAsync<SearchResponseDto>(response, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WowQueryChipDto>?> GetWowQueriesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IReadOnlyList<WowQueryChipDto>>(
            ApiRoutes.WowQueries.TrimStart('/'), ApiJson.Options, ct).ConfigureAwait(false);

    public async Task<IngestProgressDto?> StartIngestAsync(IngestRequestDto request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(ApiRoutes.Ingest.TrimStart('/'), request, ApiJson.Options, ct)
            .ConfigureAwait(false);
        return await ReadSuccessAsync<IngestProgressDto>(response, ct).ConfigureAwait(false);
    }

    public async Task<IngestProgressDto?> GetIngestAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IngestProgressDto>(ApiRoutes.Ingest.TrimStart('/'), ApiJson.Options, ct)
            .ConfigureAwait(false);

    public async Task OptimizeIndexAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.IngestOptimize.TrimStart('/'), null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task ResetIndexesAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.IngestResetIndexes.TrimStart('/'), null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task ResetCatalogAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.IngestResetCatalog.TrimStart('/'), null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<StatusDto?> GetStatusAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await _http.GetFromJsonAsync<StatusDto>(
                ApiRoutes.Status.TrimStart('/'), ApiJson.Options, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("Status request timed out after 15 seconds ({BaseAddress})", _http.BaseAddress);
            throw new ApiException(System.Net.HttpStatusCode.RequestTimeout, "API status timed out after 15 seconds.");
        }
    }

    public async Task<ModelsResponseDto?> GetModelsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<ModelsResponseDto>(ApiRoutes.Models.TrimStart('/'), ApiJson.Options, ct)
            .ConfigureAwait(false);

    public async Task<ModelSelectResultDto?> SelectModelAsync(string modelId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            ApiRoutes.ModelsSelect.TrimStart('/'),
            new ModelSelectRequestDto { ModelId = modelId },
            ApiJson.Options,
            ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<ModelSelectResultDto>(ApiJson.Options, ct).ConfigureAwait(false);

        var error = await ReadProblemMessageAsync(response, ct).ConfigureAwait(false);
        return new ModelSelectResultDto { Ok = false, Error = error };
    }

    private async Task<T?> ReadSuccessAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false);

        var message = await ReadProblemMessageAsync(response, ct).ConfigureAwait(false);
        _logger.LogError(
            "API {Method} {Uri} failed with {Status}: {Message}",
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri,
            (int)response.StatusCode,
            message);
        throw new ApiException(response.StatusCode, message);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = await ReadProblemMessageAsync(response, ct).ConfigureAwait(false);
        _logger.LogError(
            "API {Method} {Uri} failed with {Status}: {Message}",
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri,
            (int)response.StatusCode,
            message);
        throw new ApiException(response.StatusCode, message);
    }

    private static async Task<string> ReadProblemMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
                return errorProp.GetString() ?? body;
            if (root.TryGetProperty("detail", out var detailProp) && detailProp.ValueKind == JsonValueKind.String)
                return detailProp.GetString() ?? body;
            if (root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                return titleProp.GetString() ?? body;
        }
        catch (JsonException)
        {
            // fall through
        }

        return body.Length > 240 ? body[..240] + "…" : body;
    }
}
