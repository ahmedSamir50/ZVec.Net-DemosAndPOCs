using ClipOnnx.App.Encoding;
using ClipOnnx.App.Ingest;
using ClipOnnx.App.Models;
using ClipOnnx.App.Options;
using ClipOnnx.App.Services;
using ClipOnnx.App.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ZVec.NET;
using ZVec.NET.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClipOnnxOptions>(builder.Configuration.GetSection(ClipOnnxOptions.SectionName));
builder.Services.AddHttpClient("flickr", c => c.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHttpClient("models", c =>
{
    c.Timeout = TimeSpan.FromHours(2);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ZVec.ClipOnnx/1.0");
});

builder.Services.AddZVec(options =>
{
    options.LogLevel = ZVecLogLevel.Warn;
    options.QueryThreads = -1;
    options.MemoryLimitMb = 1024;
});

builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IZvecFactory>();
    var opt = sp.GetRequiredService<IOptions<ClipOnnxOptions>>().Value;
    return CollectionBootstrap.OpenOrCreateGallery(factory, opt.CollectionPath, opt.EnableMmap);
});

builder.Services.AddSingleton<ModelBootstrapStatus>();
builder.Services.AddSingleton<IngestProgressStatus>();
builder.Services.AddSingleton<IClipEncoder, ClipEncoder>();
builder.Services.AddSingleton<IFlickr8kIngestService, Flickr8kIngestService>();
builder.Services.AddSingleton<IGallerySearchService, GallerySearchService>();
builder.Services.AddHostedService<ModelBootstrapHostedService>();

var app = builder.Build();

var clipOpt = app.Services.GetRequiredService<IOptions<ClipOnnxOptions>>().Value;
Directory.CreateDirectory(clipOpt.DataRoot);
Directory.CreateDirectory(Path.Combine(clipOpt.DataRoot, "flickr8k", "images"));
Directory.CreateDirectory(Path.Combine(clipOpt.DataRoot, "state"));
Directory.CreateDirectory(Path.GetFullPath(clipOpt.ModelsDir));

app.UseDefaultFiles();
app.UseStaticFiles();

var imagesPath = Path.GetFullPath(Path.Combine(clipOpt.DataRoot, "flickr8k", "images"));
Directory.CreateDirectory(imagesPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imagesPath),
    RequestPath = "/gallery"
});

app.MapGet("/api/status", (IClipEncoder encoder, ModelBootstrapStatus bootstrap, IngestProgressStatus ingest, IOptions<ClipOnnxOptions> opt) =>
{
    var snap = bootstrap.Snapshot();
    var ing = ingest.Snapshot();
    return Results.Json(new
    {
        encoderReady = encoder.IsReady,
        encoderMessage = encoder.NotReadyReason,
        models = new
        {
            state = snap.State,
            modelsDir = snap.ModelsDir,
            message = snap.Message,
            error = snap.Error,
            overallPercent = snap.OverallPercent,
            files = snap.Files,
            autoDownload = opt.Value.AutoDownloadModels,
            repo = opt.Value.ModelRepo
        },
        ingest = new
        {
            state = ing.State,
            message = ing.Message,
            error = ing.Error,
            phaseDetail = ing.PhaseDetail,
            active = ing.Active,
            bytesReceived = ing.BytesReceived,
            bytesTotal = ing.BytesTotal,
            downloadPercent = ing.DownloadPercent,
            manifestOffset = ing.ManifestOffset,
            manifestTotal = ing.ManifestTotal,
            embeddedThisRun = ing.EmbeddedThisRun,
            skippedThisRun = ing.SkippedThisRun,
            targetThisRun = ing.TargetThisRun,
            embedPercent = ing.EmbedPercent,
            zipsDownloadedThisRun = ing.ZipsDownloadedThisRun,
            elapsedMs = ing.ElapsedMs
        },
        collectionPath = Path.GetFullPath(opt.Value.CollectionPath),
        model = "CLIP ViT-B/32 ONNX",
        dim = 512,
        preprocess = "SkiaSharp fit-contain + pad 224×224",
        dataset = "Flickr8k"
    });
});

app.MapPost("/api/ingest", (IngestRequest? body, IFlickr8kIngestService ingest, IOptions<ClipOnnxOptions> opt) =>
{
    var maxImages = ResolveMaxImages(body, opt.Value.DefaultBatchSize);
    var result = ingest.TryStartIngest(maxImages);

    if (result.Started)
        return Results.Json(new { started = true, maxImages = result.MaxImages }, statusCode: StatusCodes.Status202Accepted);

    if (string.Equals(result.Error, "Ingest already running.", StringComparison.Ordinal))
        return Results.Json(new { started = false, error = result.Error, maxImages = result.MaxImages }, statusCode: StatusCodes.Status409Conflict);

    return Results.BadRequest(new { started = false, error = result.Error, maxImages = result.MaxImages });
});

static int ResolveMaxImages(IngestRequest? body, int defaultMax)
{
    if (body?.MaxImages is > 0)
        return body.MaxImages.Value;

    // Legacy: batchSize × maxBatches
    if (body?.BatchSize is > 0 || body?.MaxBatches is > 0)
    {
        var batch = body.BatchSize is > 0 ? body.BatchSize.Value : defaultMax;
        var batches = body.MaxBatches is > 0 ? body.MaxBatches.Value : 1;
        return batch * batches;
    }

    return defaultMax;
}

app.MapPost("/api/search/text", async (TextSearchRequest body, IGallerySearchService search, IOptions<ClipOnnxOptions> opt, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Query))
        return Results.BadRequest(new { error = "query is required" });
    try
    {
        var topK = body.TopK is > 0 ? body.TopK.Value : opt.Value.DefaultTopK;
        var hits = await search.SearchTextAsync(body.Query, topK, ct);
        return Results.Json(hits);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/search/image", async (HttpRequest req, IGallerySearchService search, IOptions<ClipOnnxOptions> opt, CancellationToken ct) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "multipart form expected (file)" });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "file is required" });
    if (file.Length > opt.Value.MaxUploadBytes)
        return Results.BadRequest(new { error = $"file exceeds {opt.Value.MaxUploadBytes} bytes" });

    var topK = int.TryParse(form["topK"], out var k) && k > 0 ? k : opt.Value.DefaultTopK;
    try
    {
        await using var stream = file.OpenReadStream();
        var hits = await search.SearchImageAsync(stream, topK, ct);
        return Results.Json(hits);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

public sealed record IngestRequest(int? MaxImages, int? BatchSize, int? MaxBatches);
public sealed record TextSearchRequest(string Query, int? TopK);
