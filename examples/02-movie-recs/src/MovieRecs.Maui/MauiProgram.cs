using Microsoft.Extensions.Logging;
using MovieRecs.Maui.Encoding;
using MovieRecs.Maui.Options;
using MovieRecs.Maui.Services;
using MudBlazor.Services;
using ZVec.NET;
using ZVec.NET.DependencyInjection;

namespace MovieRecs.Maui;

/// <summary>
/// MAUI entry: MudBlazor + Blazor Hybrid + singleton MiniLM/ZVec pipeline.
/// No ASP.NET controllers — pages inject services (edge / in-process talk track).
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices();

        var options = new MovieRecsOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IngestProgressStatus>();
        builder.Services.AddSingleton<PerfMonitorService>();
        builder.Services.AddSingleton<IIndexStampStore, IndexStampStore>();
        builder.Services.AddSingleton<IMovieLensCatalog, MovieLensCatalog>();
        builder.Services.AddSingleton<IMiniLmEncoder, MiniLmEncoder>();
        builder.Services.AddSingleton<IMovieStore, MovieStore>();
        builder.Services.AddSingleton<IMovieLensIngestService, MovieLensIngestService>();
        builder.Services.AddSingleton<IRecommendService, RecommendService>();

        // In-process ZVec — no controllers; Blazor injects services directly (edge pattern).
        builder.Services.AddZVec(z =>
        {
            z.LogLevel = ZVecLogLevel.Warn;
            z.QueryThreads = -1;
            z.MemoryLimitMb = 512;
        });

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
