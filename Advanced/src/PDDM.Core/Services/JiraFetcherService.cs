using System.Net.Http.Json;
using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.Models.JiraApi;
using PDDM.Shared.Constants;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class JiraFetcherService : IJiraFetcher
{
    private readonly HttpClient _httpClient;
    private readonly PddmRuntimeSettings _runtimeSettings;

    /// <summary>Creates a Jira fetcher using the named HttpClient.</summary>
    public JiraFetcherService(IHttpClientFactory httpClientFactory, PddmRuntimeSettings runtimeSettings)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientNames.Jira);
        _runtimeSettings = runtimeSettings;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JiraIssue>> FetchByTypeAsync(string issueType, int maxTotal, CancellationToken cancellationToken = default)
    {
        var settings = _runtimeSettings.Current.Jira;
        var jql = $"project={settings.ProjectKey} AND issuetype=\"{issueType}\"";
        return FetchPaginatedAsync(jql, maxTotal, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JiraIssue>> FetchByJqlAsync(string jql, int maxTotal, CancellationToken cancellationToken = default)
        => FetchPaginatedAsync(jql, maxTotal, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<JiraIssue>> FetchEpicChildrenAsync(string epicKey, CancellationToken cancellationToken = default)
    {
        var settings = _runtimeSettings.Current.Jira;
        var jql = $"project={settings.ProjectKey} AND \"Epic Link\"={epicKey}";
        return FetchPaginatedAsync(jql, 100, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JiraIssue?> FetchSingleAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        var fields = Uri.EscapeDataString(SharedPddmDefaults.JiraIssueFields);
        var response = await _httpClient.GetAsync($"issue/{issueKey}?fields={fields}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<JiraIssue>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the Jira search relative URL (for tests and diagnostics).</summary>
    public static string BuildSearchUrl(string jql, int startAt, int maxResults)
    {
        var encoded = Uri.EscapeDataString(jql);
        var fields = Uri.EscapeDataString(SharedPddmDefaults.JiraIssueFields);
        return $"search?jql={encoded}&startAt={startAt}&maxResults={maxResults}&fields={fields}";
    }

    private async Task<IReadOnlyList<JiraIssue>> FetchPaginatedAsync(string jql, int maxTotal, CancellationToken cancellationToken)
    {
        if (maxTotal <= 0)
            return [];

        var settings = _runtimeSettings.Current.Jira;
        var all = new List<JiraIssue>();
        var startAt = 0;
        var maxResults = Math.Min(settings.MaxResultsPerRequest, maxTotal);

        while (all.Count < maxTotal)
        {
            var pageSize = Math.Min(maxResults, maxTotal - all.Count);
            var url = BuildSearchUrl(jql, startAt, pageSize);
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snippet = body.Length > 400 ? body[..400] : body;
                throw new HttpRequestException(
                    $"Jira request failed {(int)response.StatusCode} {response.ReasonPhrase} for '{url}'. Body: {snippet}");
            }

            var result = await response.Content.ReadFromJsonAsync<JiraSearchResult>(cancellationToken).ConfigureAwait(false);
            if (result is null || result.Issues.Count == 0)
                break;

            all.AddRange(result.Issues);
            startAt += result.Issues.Count;
            if (result.Issues.Count < pageSize)
                break;

            await Task.Delay(settings.RequestDelayMs, cancellationToken).ConfigureAwait(false);
        }

        return all.Count > maxTotal ? all.Take(maxTotal).ToList() : all;
    }
}
