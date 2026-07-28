using Microsoft.Extensions.Options;
using PDDM.Core.Abstractions;
using PDDM.Core.Configuration;
using PDDM.Core.DependencyInjection;
using PDDM.Core.Models;
using PDDM.Core.Storage;
using PDDM.Shared.Constants;
using ZVec.NET;
using ZVec.NET.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddControllers();
builder.Services.Configure<PddmSettings>(builder.Configuration.GetSection(ConfigurationSections.Pddm));
builder.Services.AddSingleton(sp =>
{
    var initial = sp.GetRequiredService<IOptions<PddmSettings>>().Value;
    return new PddmRuntimeSettings(initial);
});

builder.Services.AddZVec(options =>
{
    options.LogLevel = ZVecLogLevel.Warn;
    options.QueryThreads = -1;
    options.MemoryLimitMb = 512;
    options.MaxConcurrentNativeCalls = 0;
});

builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IZvecFactory>();
    var settings = sp.GetRequiredService<PddmRuntimeSettings>().Current;
    return new DocsCollectionHolder(factory, settings.ZVec.CollectionPath, settings.ZVec.EnableMmap);
});
// Prefer DocsCollectionHolder.Collection at call time (VectorStoreService); keep type for tests/compat.
builder.Services.AddSingleton<IZvecCollection<JiraDocChunk>>(sp =>
    sp.GetRequiredService<DocsCollectionHolder>().Collection);

builder.Services.AddHttpClient(HttpClientNames.LmStudio, (sp, client) =>
{
    var settings = sp.GetRequiredService<PddmRuntimeSettings>().Current.LmStudio;
    client.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
    // Embedding batches can exceed Aspire's default ~30s TotalRequestTimeout.
    client.Timeout = TimeSpan.FromMinutes(1);
})
// Strip ServiceDefaults resilience so Attempt/TotalRequestTimeout (~30s) cannot abort embeddings early.
.RemoveAllResilienceHandlers();

builder.Services.AddHttpClient(HttpClientNames.Jira, (sp, client) =>
{
    var settings = sp.GetRequiredService<PddmRuntimeSettings>().Current.Jira;
    client.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(120);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(SharedPddmDefaults.JiraUserAgent);
});

builder.Services.AddPddmCore();
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyNames.AllowUi, policy =>
    {
        policy.WithOrigins("http://localhost:5200", "https://localhost:7200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors(CorsPolicyNames.AllowUi);
app.MapDefaultEndpoints();
app.MapControllers();

var hybrid = app.Services.GetRequiredService<IHybridIndex>();
await hybrid.RebuildFromStoreAsync();

app.Run();

/// <summary>Marker for WebApplicationFactory.</summary>
public partial class Program;
