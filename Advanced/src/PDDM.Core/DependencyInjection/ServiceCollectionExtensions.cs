using Microsoft.Extensions.DependencyInjection;
using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.Services;

namespace PDDM.Core.DependencyInjection;

/// <summary>Registers PDDM Core services (excluding ZVec collection registration).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds Core abstractions and implementations.</summary>
    public static IServiceCollection AddPddmCore(this IServiceCollection services)
    {
        services.AddSingleton<IDecisionDetector, DecisionDetector>();
        services.AddSingleton<IJiraFetcher, JiraFetcherService>();
        services.AddSingleton<IChunkingService, ChunkingService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IVectorStore, VectorStoreService>();
        services.AddSingleton<IHybridIndex, HybridIndexService>();
        services.AddSingleton<IntentClassifier>();
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IIntentClassifier, HybridIntentClassifier>();
        services.AddSingleton<IContextBuilder, ContextBuilderService>();
        services.AddSingleton<INavigationEngine, NavigationEngine>();
        services.AddSingleton<IIngestionOrchestrator, IngestionOrchestrator>();
        services.AddSingleton<ISettingsService, SettingsService>();
        return services;
    }
}
