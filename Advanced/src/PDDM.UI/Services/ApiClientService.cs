using System.Net.Http.Json;
using PDDM.Shared.Constants;
using PDDM.Shared.Dtos;

namespace PDDM.UI.Services;

/// <summary>HTTP client for PDDM.Api REST endpoints.</summary>
public sealed class ApiClientService
{
    private readonly HttpClient _http;

    /// <summary>Creates the API client.</summary>
    public ApiClientService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient(HttpClientNames.PddmApi);
    }

    /// <summary>Starts ingestion.</summary>
    public async Task<IngestionProgressDto?> StartIngestionAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.Ingestion.TrimStart('/'), null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestionProgressDto>(ct).ConfigureAwait(false);
    }

    /// <summary>Gets ingestion progress.</summary>
    public async Task<IngestionProgressDto?> GetIngestionAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IngestionProgressDto>(ApiRoutes.Ingestion.TrimStart('/'), ct).ConfigureAwait(false);

    /// <summary>Gets store stats.</summary>
    public async Task<StatsDto?> GetStatsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<StatsDto>(ApiRoutes.Stats.TrimStart('/'), ct).ConfigureAwait(false);

    /// <summary>Gets LM Studio settings.</summary>
    public async Task<LmStudioSettingsDto?> GetSettingsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<LmStudioSettingsDto>(ApiRoutes.Settings.TrimStart('/'), ct).ConfigureAwait(false);

    /// <summary>Updates LM Studio settings.</summary>
    public async Task<(bool Ok, string? Error, LmStudioSettingsDto? Settings)> UpdateSettingsAsync(
        LmStudioSettingsDto settings,
        CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(ApiRoutes.Settings.TrimStart('/'), settings, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<ErrorEventDto>(ct).ConfigureAwait(false);
            return (false, err?.Message ?? response.ReasonPhrase, null);
        }

        var dto = await response.Content.ReadFromJsonAsync<LmStudioSettingsDto>(ct).ConfigureAwait(false);
        return (true, null, dto);
    }

    /// <summary>Verifies LM Studio connectivity via API.</summary>
    public async Task<bool> VerifyLmStudioAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync(ApiRoutes.SettingsVerify.TrimStart('/'), null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return false;
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(ct).ConfigureAwait(false);
        return json is not null && json.TryGetValue("reachable", out var reachable) && reachable;
    }
}
