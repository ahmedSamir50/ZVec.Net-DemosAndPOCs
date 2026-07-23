using MudBlazor.Services;
using PDDM.Shared.Constants;
using PDDM.UI.Components;
using PDDM.UI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var apiBase = builder.Configuration[$"{ConfigurationSections.PddmUi}:ApiBaseUrl"] ?? "http://localhost:5100";
builder.Services.AddHttpClient(HttpClientNames.PddmApi, client =>
{
    client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(60);
})
// Aspire ServiceDefaults adds a ~30s TotalRequestTimeout which aborts long Ingest POSTs.
.RemoveAllResilienceHandlers();

builder.Services.AddScoped<ApiClientService>();
builder.Services.AddScoped<ChatClientService>();
builder.Services.AddScoped<PDDM.Shared.Sse.SseEventParser>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapDefaultEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
