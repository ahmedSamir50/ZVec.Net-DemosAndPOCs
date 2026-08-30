using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Data;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Services;
using ProductSearch.Core.Storage;
using ZVec.NET;
using ZVec.NET.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace ProductSearch.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProductSearchCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProductSearchOptions>(configuration.GetSection(ProductSearchOptions.SectionName));

        services.AddHttpClient("models", c =>
        {
            c.Timeout = TimeSpan.FromHours(2);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZVec.ProductSearch/1.0");
        });
        services.AddHttpClient("fashion", c => c.Timeout = TimeSpan.FromMinutes(30));

        services.AddZVec(options =>
        {
            options.LogLevel = ZVecLogLevel.Warn;
            options.QueryThreads = -1;
            options.MemoryLimitMb = 1024;
        });

        services.AddDbContextFactory<ProductDbContext>((sp, options) =>
        {
            var searchOptions = sp.GetRequiredService<IOptions<ProductSearchOptions>>().Value;
            options.UseNpgsql(searchOptions.PostgresConnectionString, o =>
            {
                o.UseVector();
                o.MigrationsAssembly(typeof(ProductDbContext).Assembly.FullName);
            });
        });

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProductSearchOptions>>().Value);
        services.AddSingleton<FashionCatalogReader>();
        services.AddSingleton<FashionDatasetDownloader>();
        services.AddSingleton<DualCollectionHolder>();
        services.AddSingleton<IIndexStampStore, IndexStampStore>();
        services.AddSingleton<ModelBootstrapStatus>();
        services.AddSingleton<IngestProgressStatus>();
        services.AddSingleton<ISigLipEncoder, SigLipEncoder>();
        services.AddSingleton<ISigLipModelSelectionService, SigLipModelSelectionService>();
        services.AddSingleton<IIngestService, IngestService>();
        services.AddSingleton<IProductSearchService, ProductSearchService>();
        services.AddSingleton<IStatusService, StatusService>();
        services.AddSingleton<ICatalogMaintenanceService, CatalogMaintenanceService>();
        services.AddHostedService<ModelBootstrapHostedService>();

        return services;
    }
}
