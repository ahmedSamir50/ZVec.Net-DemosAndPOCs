using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Shared.Dtos;

namespace PDDM.Core.Services;

/// <inheritdoc />
public sealed class SettingsService : ISettingsService
{
    private readonly PddmRuntimeSettings _runtimeSettings;
    private readonly IEmbeddingService _embeddingService;

    /// <summary>Creates the settings service.</summary>
    public SettingsService(PddmRuntimeSettings runtimeSettings, IEmbeddingService embeddingService)
    {
        _runtimeSettings = runtimeSettings;
        _embeddingService = embeddingService;
    }

    /// <inheritdoc />
    public LmStudioSettingsDto GetLmStudioSettings()
    {
        var lm = _runtimeSettings.Current.LmStudio;
        return new LmStudioSettingsDto
        {
            BaseUrl = lm.BaseUrl,
            EmbeddingModel = lm.EmbeddingModel,
            ChatModel = lm.ChatModel,
            EmbeddingDimensions = lm.EmbeddingDimensions,
            ChatTemperature = lm.ChatTemperature,
            ChatMaxTokens = lm.ChatMaxTokens,
            EmbeddingBatchSize = lm.EmbeddingBatchSize
        };
    }

    /// <inheritdoc />
    public void UpdateLmStudioSettings(LmStudioSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _runtimeSettings.UpdateLmStudio(new LmStudioSettings
        {
            BaseUrl = settings.BaseUrl,
            EmbeddingModel = settings.EmbeddingModel,
            ChatModel = settings.ChatModel,
            EmbeddingDimensions = settings.EmbeddingDimensions,
            ChatTemperature = settings.ChatTemperature,
            ChatMaxTokens = settings.ChatMaxTokens,
            EmbeddingBatchSize = settings.EmbeddingBatchSize
        });
    }

    /// <inheritdoc />
    public Task<bool> VerifyLmStudioAsync(CancellationToken cancellationToken = default)
        => _embeddingService.VerifyLmStudioAsync(cancellationToken);
}
