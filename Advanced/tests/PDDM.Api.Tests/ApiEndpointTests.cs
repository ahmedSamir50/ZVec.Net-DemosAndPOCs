using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using PDDM.Core.Abstractions;
using PDDM.Core.Models;
using PDDM.Core.Storage;
using PDDM.Shared;
using PDDM.Shared.Constants;
using PDDM.Shared.Dtos;
using ZVec.NET;
using ZVec.NET.Mapping;

namespace PDDM.Api.Tests;

public class ApiEndpointTests : IClassFixture<PddmApiFactory>
{
    private readonly HttpClient _client;
    private readonly PddmApiFactory _factory;

    public ApiEndpointTests(PddmApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSettings_ReturnsOk()
    {
        var dto = await _client.GetFromJsonAsync<LmStudioSettingsDto>(ApiRoutes.Settings);
        dto.Should().NotBeNull();
        dto!.EmbeddingDimensions.Should().Be(768);
    }

    [Fact]
    public async Task PutSettings_RejectsBadDimension()
    {
        var response = await _client.PutAsJsonAsync(ApiRoutes.Settings, new LmStudioSettingsDto { EmbeddingDimensions = 32 });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStats_ReturnsOk()
    {
        var dto = await _client.GetFromJsonAsync<StatsDto>(ApiRoutes.Stats);
        dto.Should().NotBeNull();
    }

    [Fact]
    public async Task GetIngestion_ReturnsOk()
    {
        var dto = await _client.GetFromJsonAsync<IngestionProgressDto>(ApiRoutes.Ingestion);
        dto.Should().NotBeNull();
    }

    [Fact]
    public async Task PostIngestion_ReturnsOk()
    {
        var response = await _client.PostAsync(ApiRoutes.Ingestion, null);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<IngestionProgressDto>();
        dto!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task PostSettingsVerify_ReturnsOk()
    {
        var response = await _client.PostAsync(ApiRoutes.SettingsVerify, null);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChatStream_ReturnsSseEvents()
    {
        var response = await _client.GetAsync($"{ApiRoutes.ChatStream}?question={Uri.EscapeDataString("I need to add validation")}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(SseEventTypes.Intent);
        body.Should().Contain(SseEventTypes.Progress);
        body.Should().Contain(ChatProgressPhases.Classifying);
        body.Should().Contain(SseEventTypes.Done);
    }
}

/// <summary>Test host that replaces ZVec and external services with substitutes.</summary>
public sealed class PddmApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            RemoveAll(services, typeof(IZvecCollection<JiraDocChunk>));
            RemoveAll(services, typeof(DocsCollectionHolder));
            RemoveAll(services, typeof(IZvecFactory));
            RemoveAll(services, typeof(IVectorStore));
            RemoveAll(services, typeof(IHybridIndex));
            RemoveAll(services, typeof(IEmbeddingService));
            RemoveAll(services, typeof(IJiraFetcher));
            RemoveAll(services, typeof(IIngestionOrchestrator));
            RemoveAll(services, typeof(IChatService));
            RemoveAll(services, typeof(INavigationEngine));

            var collection = Substitute.For<IZvecCollection<JiraDocChunk>>();
            services.AddSingleton(collection);

            var factory = Substitute.For<IZvecFactory>();
            services.AddSingleton(factory);

            var store = Substitute.For<IVectorStore>();
            store.LoadChunkIdsAsync(Arg.Any<CancellationToken>()).Returns([]);
            services.AddSingleton(store);

            var hybrid = Substitute.For<IHybridIndex>();
            hybrid.RebuildFromStoreAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            hybrid.GetByTier(Arg.Any<int>()).Returns([]);
            hybrid.TotalCount.Returns(0);
            services.AddSingleton(hybrid);

            var embed = Substitute.For<IEmbeddingService>();
            embed.VerifyLmStudioAsync(Arg.Any<CancellationToken>()).Returns(true);
            embed.EmbedSingleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReadOnlyMemory<float>(new float[768]));
            services.AddSingleton(embed);

            var jira = Substitute.For<IJiraFetcher>();
            services.AddSingleton(jira);

            var ingestion = Substitute.For<IIngestionOrchestrator>();
            ingestion.RunAsync(Arg.Any<CancellationToken>()).Returns(new IngestionProgress { Status = "Completed" });
            ingestion.GetProgress().Returns(new IngestionProgress { Status = "NotStarted" });
            services.AddSingleton(ingestion);

            var chat = Substitute.For<IChatService>();
            chat.StreamAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(StreamTokens("hello"));
            services.AddSingleton(chat);

            var nav = Substitute.For<INavigationEngine>();
            nav.NavigateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new NavigatedContext
                {
                    Intent = QueryIntent.NewRequirement,
                    AssembledContext = "context"
                });
            services.AddSingleton(nav);
        });
    }

    private static async IAsyncEnumerable<string> StreamTokens(string token)
    {
        yield return token;
        await Task.CompletedTask;
    }

    private static void RemoveAll(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }
}
