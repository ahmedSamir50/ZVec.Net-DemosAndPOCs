using System.Diagnostics;
using ClipOnnx.App.Encoding;
using ClipOnnx.App.Ingest;
using ClipOnnx.App.DataModels;
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

builder.Services.AddSingleton<GalleryStore>();
builder.Services.AddSingleton<IGalleryStampStore, GalleryStampStore>();
builder.Services.AddSingleton<ModelBootstrapStatus>();
builder.Services.AddSingleton<IngestProgressStatus>();
builder.Services.AddSingleton<IFlickrCaptionLookup, FlickrCaptionLookup>();
builder.Services.AddSingleton<IClipEncoder, ClipEncoder>();
builder.Services.AddSingleton<IClipModelSelectionService, ClipModelSelectionService>();
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

app.MapGet("/api/models", (IClipModelSelectionService models) =>
    Results.Json(new { models = models.ListExpectations(), activeModelId = models.ActiveDefinition.Id }));

app.MapPost("/api/models/select", async (ModelSelectRequest body, IClipModelSelectionService models, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.ModelId))
        return Results.BadRequest(new { error = "modelId is required" });
    var result = await models.SelectAsync(body.ModelId.Trim(), ct);
    return result.Ok ? Results.Json(result) : Results.BadRequest(result);
});

app.MapGet("/api/status", (
    IClipEncoder encoder,
    ModelBootstrapStatus bootstrap,
    IngestProgressStatus ingest,
    IOptions<ClipOnnxOptions> opt,
    IClipModelSelectionService models,
    IGalleryStampStore stamp,
    GalleryStore gallery) =>
{
    var snap = bootstrap.Snapshot();
    var ing = ingest.Snapshot();
    var active = models.ActiveDefinition;
    var st = stamp.Load();
    var mismatch = stamp.IsMismatch(active, st);
    var warnings = new List<string>();
    if (mismatch)
        warnings.Add(stamp.MismatchMessage(active, st)!);
    else if (st.Offset <= 0)
        warnings.Add("Gallery index is empty. Click Ingest before searching.");
    else if (st.Offset < 200)
        warnings.Add($"Only {st.Offset} images indexed — expect weak recall until you ingest more.");

    return Results.Json(new
    {
        encoderReady = encoder.IsReady,
        encoderMessage = encoder.NotReadyReason,
        demoReady = encoder.IsReady && !mismatch && st.Offset > 0,
        activeModel = new
        {
            id = active.Id,
            displayName = active.DisplayName,
            embeddingDim = active.EmbeddingDim,
            expectations = models.ExpectationsFor(active.Id)
        },
        gallery = new
        {
            collectionPath = gallery.CollectionPath,
            indexedApprox = st.Offset,
            stampModelId = st.ModelId,
            stampDim = st.EmbeddingDim,
            stampPipeline = st.EncodePipelineVersion,
            modelMismatch = mismatch,
            mismatchMessage = stamp.MismatchMessage(active, st),
            minCosine = opt.Value.MinCosine,
            maxCosineGapFromTop = opt.Value.MaxCosineGapFromTop,
            minConfidentHits = opt.Value.MinConfidentHits
        },
        modelsCatalog = models.ListExpectations(),
        models = new
        {
            state = snap.State,
            modelsDir = snap.ModelsDir,
            message = snap.Message,
            error = snap.Error,
            overallPercent = snap.OverallPercent,
            files = snap.Files,
            autoDownload = opt.Value.AutoDownloadModels,
            repo = active.HfRepo
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
        warnings,
        offline = true,
        model = active.DisplayName + " ONNX (CPU)",
        dim = active.EmbeddingDim,
        preprocess = "SkiaSharp center-crop 224×224",
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

app.MapPost("/api/ingest/reset", (IFlickr8kIngestService ingest) =>
{
    var result = ingest.TryResetIndex();
    if (result.Reset)
        return Results.Json(new { reset = true });

    if (string.Equals(result.Error, "Ingest already running.", StringComparison.Ordinal))
        return Results.Json(new { reset = false, error = result.Error }, statusCode: StatusCodes.Status409Conflict);

    return Results.BadRequest(new { reset = false, error = result.Error });
});

app.MapPost("/api/optimize", (IFlickr8kIngestService ingest) =>
{
    var result = ingest.TryOptimize();
    if (result.Ok)
        return Results.Json(new { optimized = true });

    if (string.Equals(result.Error, "Ingest already running.", StringComparison.Ordinal))
        return Results.Json(new { optimized = false, error = result.Error }, statusCode: StatusCodes.Status409Conflict);

    return Results.BadRequest(new { optimized = false, error = result.Error });
});

static int ResolveMaxImages(IngestRequest? body, int defaultMax)
{
    if (body?.MaxImages is > 0)
        return body.MaxImages.Value;

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
        float? minCosine = body.MinCosine is >= 0 and <= 1 ? body.MinCosine : null;
        var result = await search.SearchTextAsync(body.Query, topK, minCosine, ct);
        return Results.Json(result);
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
    float? minCosine = float.TryParse(form["minCosine"], out var mc) && mc is >= 0 and <= 1 ? mc : null;
    try
    {
        await using var stream = file.OpenReadStream();
        var result = await search.SearchImageAsync(stream, topK, minCosine, ct);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/debug/probe", async (string? q, int? topK, IGallerySearchService search, IClipEncoder encoder, CancellationToken ct) =>
{
    if (!encoder.IsReady)
        return Results.BadRequest(new { error = encoder.NotReadyReason ?? "encoder not ready" });
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "q is required" });

    try
    {
        var result = await search.ProbeAsync(q, topK is > 0 ? topK.Value : 5, ct);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

/// <summary>
/// Mutual CLIP cosine between a gallery image and text — proves vision↔text space for the active model.
/// </summary>
app.MapGet("/api/debug/encode-check", (
    string? file,
    string? text,
    IClipEncoder encoder,
    IOptions<ClipOnnxOptions> opt) =>
{
    if (!encoder.IsReady)
        return Results.BadRequest(new { error = encoder.NotReadyReason ?? "encoder not ready", ok = false });

    var imagesDir = Path.GetFullPath(Path.Combine(opt.Value.DataRoot, "flickr8k", "images"));
    string path;
    if (!string.IsNullOrWhiteSpace(file))
    {
        path = Path.Combine(imagesDir, Path.GetFileName(file));
        if (!File.Exists(path))
            return Results.BadRequest(new { error = $"file not found: {file}", ok = false });
    }
    else
    {
        path = Directory.EnumerateFiles(imagesDir, "*.jpg").FirstOrDefault()
               ?? Directory.EnumerateFiles(imagesDir, "*.jpeg").FirstOrDefault()
               ?? "";
        if (string.IsNullOrEmpty(path))
            return Results.BadRequest(new { error = "No images under flickr8k/images — ingest/download first.", ok = false });
    }

    var prompt = string.IsNullOrWhiteSpace(text) ? "a photo of a dog" : text.Trim();
    var sw = Stopwatch.StartNew();
    var img = encoder.EncodeImage(path);
    var txt = encoder.EncodeText(prompt);
    sw.Stop();

    double dot = 0;
    for (var i = 0; i < img.Length; i++)
        dot += img[i] * (double)txt[i];
    var cosine = (float)Math.Clamp(dot, -1, 1);
    var ok = cosine >= 0.15f; // soft floor; clear matches usually higher
    var pass = cosine >= 0.22f;

    return Results.Json(new
    {
        ok = pass,
        softOk = ok,
        cosine,
        similarityPercent = ClipScoreSemantics.SimilarityPercent(cosine),
        prompt,
        file = Path.GetFileName(path),
        modelId = encoder.ActiveModelId,
        embeddingDim = encoder.EmbeddingDim,
        encodeMs = sw.ElapsedMilliseconds,
        reminder = pass
            ? "Encode path looks healthy for this pair."
            : "Low mutual cosine — check preprocess, model files, or pick a clearly matching image/text pair. After model change: Reset → Ingest."
    });
});

app.MapGet("/api/debug/sanity", async (IGallerySearchService search, IClipEncoder encoder, CancellationToken ct) =>
{
    if (!encoder.IsReady)
        return Results.BadRequest(new { error = encoder.NotReadyReason ?? "encoder not ready" });

    const float dogsMinTop = 0.28f;
    const float networkMaxTop = 0.30f;
    try
    {
        var dogs = await search.ProbeAsync("dogs", 3, ct);
        var network = await search.ProbeAsync("network", 3, ct);
        var dogsTop = dogs.Hits.FirstOrDefault()?.Cosine ?? 0f;
        var networkTop = network.Hits.FirstOrDefault()?.Cosine ?? 0f;
        var ok = dogs.Hits.Count > 0 && dogsTop >= dogsMinTop && networkTop <= networkMaxTop;
        string? failReason = null;
        if (dogs.EmptyMessage is not null && dogs.Hits.Count == 0 && dogs.EmptyMessage.Contains("Reset", StringComparison.OrdinalIgnoreCase))
            failReason = dogs.EmptyMessage;
        else if (dogs.Hits.Count == 0)
            failReason = "No hits for “dogs” — index empty or mismatched? Reset index → Ingest.";
        else if (dogsTop < dogsMinTop)
            failReason = $"“dogs” top cosine {dogsTop:F3} < {dogsMinTop} — re-index after model change, or ingest more.";
        else if (networkTop > networkMaxTop)
            failReason = $"“network” top cosine {networkTop:F3} > {networkMaxTop} — filters should hide this in search UI.";

        return Results.Json(new
        {
            ok,
            failReason,
            thresholds = new { dogsMinTop, networkMaxTop },
            dogs = new { topCosine = dogsTop, hits = dogs.Hits },
            network = new { topCosine = networkTop, hits = network.Hits },
            modelId = encoder.ActiveModelId,
            reminder = "After model change: Reset index → Ingest. Pre-ingest L/14 before live demos."
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

public sealed record IngestRequest(int? MaxImages, int? BatchSize, int? MaxBatches);
public sealed record TextSearchRequest(string Query, int? TopK, float? MinCosine);
public sealed record ModelSelectRequest(string ModelId);
