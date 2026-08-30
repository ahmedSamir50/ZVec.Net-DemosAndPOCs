using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ProductSearch.Api;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;
using ProductSearch.Core.DependencyInjection;
using ProductSearch.Shared.Constants;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();
builder.Services.Configure<ProductSearchOptions>(
    builder.Configuration.GetSection(ConfigurationSections.ProductSearch));
builder.Services.PostConfigure<ProductSearchOptions>(options =>
{
    options.PostgresConnectionString = builder.Configuration.GetConnectionString("productsearch")
        ?? options.PostgresConnectionString;
});

builder.Services.AddProductSearchCore(builder.Configuration);
builder.Services.AddSingleton<WowQueryProvider>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyNames.AllowUi, policy =>
    {
        policy.WithOrigins("http://localhost:5210", "https://localhost:7210")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<ProductSearchOptions>>().Value;
Directory.CreateDirectory(Path.GetFullPath(options.DataRoot));
Directory.CreateDirectory(Path.GetFullPath(options.ModelsDir));
Directory.CreateDirectory(Path.GetFullPath(options.CatalogCachePath));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductDbContext>>();
    await using var context = await db.CreateDbContextAsync().ConfigureAwait(false);
    await context.Database.MigrateAsync().ConfigureAwait(false);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicyNames.AllowUi);

var catalogPath = Path.GetFullPath(options.CatalogCachePath);
Directory.CreateDirectory(catalogPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(catalogPath),
    RequestPath = "/catalog-cache"
});

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();

public partial class Program;
