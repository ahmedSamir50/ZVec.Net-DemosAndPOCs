using System.Net.Http.Json;
using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.Constants;
using PDDM.Core.Models.LmStudio;
using PDDM.Shared.Constants;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class EmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PddmRuntimeSettings _runtimeSettings;

    /// <summary>Creates the embedding service.</summary>
    public EmbeddingService(IHttpClientFactory httpClientFactory, PddmRuntimeSettings runtimeSettings)
    {
        _httpClientFactory = httpClientFactory;
        _runtimeSettings = runtimeSettings;
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> EmbedSingleAsync(string text, CancellationToken cancellationToken = default)
    {
        var batch = await EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);
        return batch[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return [];

        var lm = _runtimeSettings.Current.LmStudio;
        var client = _httpClientFactory.CreateClient(HttpClientNames.LmStudio);
        var request = new EmbeddingRequest
        {
            Model = lm.EmbeddingModel,
            Input = texts.ToList()
        };

        using var response = await client.PostAsJsonAsync("embeddings", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Empty embedding response from LM Studio.");

        var ordered = payload.Data.OrderBy(d => d.Index).ToList();
        var results = new List<ReadOnlyMemory<float>>(ordered.Count);
        foreach (var item in ordered)
        {
            if (item.Embedding.Count != PddmDefaults.EmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding dimension mismatch: got {item.Embedding.Count}, expected {PddmDefaults.EmbeddingDimensions}. Re-ingest required to change dimensions.");
            }

            results.Add(item.Embedding.ToArray());
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyLmStudioAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientNames.LmStudio);
            using var response = await client.GetAsync("models", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
