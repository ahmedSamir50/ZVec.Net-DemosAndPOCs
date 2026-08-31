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
    builder.Configuration.GetSection(ProductSearchOptions.SectionName));
builder.Services.PostConfigure<ProductSearchOptions>(options =>
{
    options.PostgresConnectionString = builder.Configuration.GetConnectionString("productsearch")
        ?? options.PostgresConnectionString;

    ProductSearchOptions.ResolveRelativePaths(options, builder.Environment.ContentRootPath);

    var zvec = builder.Configuration.GetSection($"{ProductSearchOptions.SectionName}:ZVec");
    if (zvec.Exists())
    {
        var textRoot = zvec["TextCollectionRoot"];
        if (!string.IsNullOrWhiteSpace(textRoot))
            options.TextCollectionRoot = ProductSearchOptions.ResolvePath(textRoot, builder.Environment.ContentRootPath);

        var imageRoot = zvec["ImageCollectionRoot"];
        if (!string.IsNullOrWhiteSpace(imageRoot))
            options.ImageCollectionRoot = ProductSearchOptions.ResolvePath(imageRoot, builder.Environment.ContentRootPath);

        if (bool.TryParse(zvec["EnableMmap"], out var enableMmap))
            options.EnableMmap = enableMmap;
    }
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddProductSearchCore(builder.Configuration);
builder.Services.AddSingleton<WowQueryProvider>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyNames.AllowUi, policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(static origin =>
            {
                if (string.IsNullOrWhiteSpace(origin))
                    return false;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                    return false;
                return uri.Host is "localhost" or "127.0.0.1";
            });
        }
        else
        {
            policy.WithOrigins("http://localhost:5210", "https://localhost:7210");
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<ProductSearchOptions>>().Value;
Directory.CreateDirectory(options.DataRoot);
Directory.CreateDirectory(options.ModelsDir);
Directory.CreateDirectory(options.CatalogCachePath);
Directory.CreateDirectory(options.TextCollectionRoot);
Directory.CreateDirectory(options.ImageCollectionRoot);

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductDbContext>>();
    await using var context = await db.CreateDbContextAsync().ConfigureAwait(false);
    var pending = await context.Database.GetPendingMigrationsAsync().ConfigureAwait(false);
    if (pending.Any())
        logger.LogInformation("Applying EF migrations: {Migrations}", string.Join(", ", pending));
    await context.Database.MigrateAsync().ConfigureAwait(false);
    logger.LogInformation("Database schema up to date.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicyNames.AllowUi);

Directory.CreateDirectory(options.CatalogCachePath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(options.CatalogCachePath),
    RequestPath = "/catalog-cache"
});

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();

public partial class Program;
