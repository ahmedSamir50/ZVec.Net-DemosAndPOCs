using ClipOnnx.App.DataModels;
using ClipOnnx.App.Encoding;
using ClipOnnx.App.Options;
using ClipOnnx.App.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ZVec.NET;
using ZVec.NET.DependencyInjection;

namespace ClipOnnx.Tests;

public sealed class ZVecClipRetrievalQualityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string? _modelsDir;
    private readonly string? _imagesDir;
    private readonly string _tempCollectionPath;
    private readonly ServiceProvider _serviceProvider;
    private readonly IZvecFactory _factory;

    public ZVecClipRetrievalQualityTests(ITestOutputHelper output)
    {
        _output = output;
        _modelsDir = FindRepoPath("examples/01-clip-onnx/src/ClipOnnx.App/models");
        _imagesDir = FindRepoPath("examples/01-clip-onnx/src/ClipOnnx.App/data/flickr8k/images");

        _tempCollectionPath = Path.Combine(Path.GetTempPath(), "zvec_clip_test_" + Guid.NewGuid().ToString("N"));

        var services = new ServiceCollection();
        services.AddZVec(options =>
        {
            options.LogLevel = ZVecLogLevel.Warn;
        });
        _serviceProvider = services.BuildServiceProvider();
        _factory = _serviceProvider.GetRequiredService<IZvecFactory>();
    }

    [Fact]
    public async Task Clip_TextToImage_And_ImageToImage_Retrieval_Quality_On_ZVec()
    {
        // 1. Graceful Bypass if models or test images are not present
        if (string.IsNullOrEmpty(_modelsDir) || !Directory.Exists(_modelsDir))
        {
            Xunit.Assert.Skip("CLIP models directory not found relative to test runner. Skipping test.");
            return;
        }

        var modelDef = ClipModelCatalog.Get("clip-vit-b16");
        var activeModelDir = Path.Combine(_modelsDir, modelDef.Id);
        var visionPath = Path.Combine(activeModelDir, "vision_model.onnx");
        var textPath = Path.Combine(activeModelDir, "text_model.onnx");

        if (!File.Exists(visionPath) || !File.Exists(textPath))
        {
            Xunit.Assert.Skip($"CLIP model weights not downloaded in {activeModelDir}. Skipping test.");
            return;
        }

        if (string.IsNullOrEmpty(_imagesDir) || !Directory.Exists(_imagesDir))
        {
            Xunit.Assert.Skip("Flickr8k images directory not found relative to test runner. Skipping test.");
            return;
        }

        // 2. Locate 3 distinct test images
        var imgGirl = Path.Combine(_imagesDir, "1000268201_693b08cb0e.jpg"); // little girl in pink dress
        var imgDogs = Path.Combine(_imagesDir, "1001773457_577c3a7d70.jpg"); // two dogs on road
        var imgPaint = Path.Combine(_imagesDir, "1002674143_1b742ab4b8.jpg"); // child with paint

        if (!File.Exists(imgGirl) || !File.Exists(imgDogs) || !File.Exists(imgPaint))
        {
            Xunit.Assert.Skip("Sample test images not found in Flickr8k directory. Skipping test.");
            return;
        }

        // 3. Initialize ClipEncoder
        var options = new ClipOnnxOptions { ActiveModelId = modelDef.Id };
        var encoder = new ClipEncoder(Options.Create(options), NullLogger<ClipEncoder>.Instance);
        encoder.InitializeFromDisk(activeModelDir, modelDef);

        Assert.True(encoder.IsReady, "ClipEncoder should be ready after initialization.");

        // 4. Create isolated scratch ZVec collection for ImageAsset512
        var collection = CollectionBootstrap.OpenOrCreate<ImageAsset512>(_factory, _tempCollectionPath, enableMmap: false);

        try
        {
            // Ingest the 3 images
            var girlVec = encoder.EncodeImage(imgGirl);
            var dogsVec = encoder.EncodeImage(imgDogs);
            var paintVec = encoder.EncodeImage(imgPaint);

            var ct = TestContext.Current.CancellationToken;
            await collection.UpsertAsync(new ImageAsset512 { Id = "girl_pink_dress", Embedding = girlVec }, ct);
            await collection.UpsertAsync(new ImageAsset512 { Id = "two_dogs", Embedding = dogsVec }, ct);
            await collection.UpsertAsync(new ImageAsset512 { Id = "child_paint", Embedding = paintVec }, ct);

            // --- TEST CASE 1: Text => Image Query: "dogs playing on the road" ---
            var queryDogs = "a photo of two dogs playing on the road";
            var queryDogsVec = encoder.EncodeText(queryDogs);

            var hitsDogs = await collection.QueryAsync(p => p.Embedding, queryDogsVec, topK: 3, ct: ct);

            Assert.NotEmpty(hitsDogs);
            Assert.Equal("two_dogs", hitsDogs[0].Record.Id);
            // In CLIP, a positive match cosine distance is typically < 0.75 (cosine similarity > 0.25)
            Assert.True(hitsDogs[0].Score < 0.80f, $"Top hit score {hitsDogs[0].Score} should indicate strong CLIP alignment.");
            _output.WriteLine($"[PASS] Text 'dogs playing' matched 'two_dogs' at Rank 1 (Distance: {hitsDogs[0].Score:F4})");

            // --- TEST CASE 2: Text => Image Query: "a little girl in pink dress" ---
            var queryGirl = "a photo of a little girl in pink dress";
            var queryGirlVec = encoder.EncodeText(queryGirl);

            var hitsGirl = await collection.QueryAsync(p => p.Embedding, queryGirlVec, topK: 3, ct: ct);

            Assert.NotEmpty(hitsGirl);
            Assert.Equal("girl_pink_dress", hitsGirl[0].Record.Id);
            _output.WriteLine($"[PASS] Text 'girl in pink dress' matched 'girl_pink_dress' at Rank 1 (Distance: {hitsGirl[0].Score:F4})");

            // --- TEST CASE 3: Image => Image Query: Query with the dogs image ---
            var hitsImgDogs = await collection.QueryAsync(p => p.Embedding, dogsVec, topK: 3, ct: ct);

            Assert.NotEmpty(hitsImgDogs);
            Assert.Equal("two_dogs", hitsImgDogs[0].Record.Id);
            Assert.True(hitsImgDogs[0].Score < 0.01f, "Identical image query should have ~0 cosine distance.");
            _output.WriteLine($"[PASS] Image 'two_dogs' matched itself at Rank 1 (Distance: {hitsImgDogs[0].Score:F4})");
        }
        finally
        {
            collection.Dispose();
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_tempCollectionPath))
        {
            try { Directory.Delete(_tempCollectionPath, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? FindRepoPath(string relativeTarget)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativeTarget);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return Path.GetFullPath(candidate);
            current = current.Parent;
        }
        return null;
    }
}
