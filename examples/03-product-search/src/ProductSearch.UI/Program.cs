using MudBlazor.Services;
using ProductSearch.Shared.Constants;
using ProductSearch.UI.Components;
using ProductSearch.UI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var apiBase = builder.Configuration[$"{ConfigurationSections.ProductSearchUi}:ApiBaseUrl"] ?? "http://localhost:5110";
builder.Services.AddHttpClient(HttpClientNames.ProductSearchApi, client =>
{
    client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(60);
})
.RemoveAllResilienceHandlers();

builder.Services.AddScoped<ApiClientService>();

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
