using System.Net.Http.Json;
using ProductSearch.Shared.Constants;
using ProductSearch.Shared.Dtos;

namespace ProductSearch.UI.Services;

/// <summary>HTTP client for ProductSearch.Api REST endpoints.</summary>
public sealed class ApiClientService
{
    private readonly HttpClient _http;

    public ApiClientService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient(HttpClientNames.ProductSearchApi);
    }

    public async Task<SearchResponseDto?> SearchAsync(SearchRequestDto request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(ApiRoutes.Search.TrimStart('/'), request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SearchResponseDto>(ct).ConfigureAwait(false);
    }

    public async Task<SearchResponseDto?> SimilarAsync(Guid productId, SearchRequestDto request, CancellationToken ct = default)
    {
        var route = $"{ApiRoutes.Search}/similar/{productId}".TrimStart('/');
        var response = await _http.PostAsJsonAsync(route, request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SearchResponseDto>(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WowQueryChipDto>?> GetWowQueriesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IReadOnlyList<WowQueryChipDto>>(ApiRoutes.WowQueries.TrimStart('/'), ct).ConfigureAwait(false);

    public async Task<IngestProgressDto?> StartIngestAsync(IngestRequestDto request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(ApiRoutes.Ingest.TrimStart('/'), request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestProgressDto>(ct).ConfigureAwait(false);
    }

    public async Task<IngestProgressDto?> GetIngestAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IngestProgressDto>(ApiRoutes.Ingest.TrimStart('/'), ct).ConfigureAwait(false);

    public async Task OptimizeIndexAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.IngestOptimize.TrimStart('/'), null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetIndexesAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.IngestResetIndexes.TrimStart('/'), null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetCatalogAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.IngestResetCatalog.TrimStart('/'), null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<StatusDto?> GetStatusAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<StatusDto>(ApiRoutes.Status.TrimStart('/'), ct).ConfigureAwait(false);

    public async Task<ModelsResponseDto?> GetModelsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<ModelsResponseDto>(ApiRoutes.Models.TrimStart('/'), ct).ConfigureAwait(false);

    public async Task<ModelSelectResultDto?> SelectModelAsync(string modelId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            ApiRoutes.ModelsSelect.TrimStart('/'),
            new ModelSelectRequestDto { ModelId = modelId },
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<ModelSelectResultDto>(ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ModelSelectResultDto>(ct).ConfigureAwait(false);
    }
}
