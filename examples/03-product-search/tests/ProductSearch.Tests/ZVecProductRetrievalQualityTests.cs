using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Encoding;
using ProductSearch.Core.Models;
using Xunit;
using ZVec.NET;
using ZVec.NET.DependencyInjection;

namespace ProductSearch.Tests;

public sealed class ZVecProductRetrievalQualityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string? _modelsDir;
    private readonly string? _dataZipPath;
    private readonly string _tempCollectionPath;
    private readonly ServiceProvider _serviceProvider;
    private readonly IZvecFactory _factory;

    public ZVecProductRetrievalQualityTests(ITestOutputHelper output)
    {
        _output = output;
        _modelsDir = FindRepoPath("examples/03-product-search/src/ProductSearch.Api/models");
        _dataZipPath = FindRepoPath("examples/03-product-search/data/fashion-10k.zip");

        _tempCollectionPath = Path.Combine(Path.GetTempPath(), "zvec_product_test_" + Guid.NewGuid().ToString("N"));

        var services = new ServiceCollection();
        services.AddZVec(options =>
        {
            options.LogLevel = ZVecLogLevel.Warn;
        });
        _serviceProvider = services.BuildServiceProvider();
        _factory = _serviceProvider.GetRequiredService<IZvecFactory>();
    }

    [Fact]
    public async Task SigLip_TextToImage_CrossModal_Retrieval_On_ZVec()
    {
        // 1. Graceful Bypass if models or dataset are not found
        if (string.IsNullOrEmpty(_modelsDir) || !Directory.Exists(_modelsDir))
        {
            Assert.Skip("ProductSearch models directory not found. Skipping test.");
            return;
        }

        var modelDef = SigLipModelCatalog.Get("siglip-base-patch16-224");
        var activeModelDir = Path.Combine(_modelsDir, modelDef.Id);
        var visionPath = Path.Combine(activeModelDir, "vision_model.onnx");
        var textPath = Path.Combine(activeModelDir, "text_model.onnx");

        if (!File.Exists(visionPath) || !File.Exists(textPath))
        {
            Assert.Skip($"SigLIP model weights not found in {activeModelDir}. Skipping test.");
            return;
        }

        if (string.IsNullOrEmpty(_dataZipPath) || !File.Exists(_dataZipPath))
        {
            Assert.Skip("fashion-10k.zip data pack not found. Skipping test.");
            return;
        }

        // 2. Load 3 real products from fashion-10k.zip
        using var zip = ZipFile.OpenRead(_dataZipPath);
        var necklaceBytes = ExtractZipEntry(zip, "images/54147.jpg"); // Femella White Necklace
        var shoeBytes = ExtractZipEntry(zip, "images/6671.jpg");      // Nike Men's Backboard Shoe
        var shirtBytes = ExtractZipEntry(zip, "images/19702.jpg");    // United Colors Of Benetton Striped Shirt

        // 3. Initialize SigLipEncoder
        var options = new ProductSearchOptions { ActiveModelId = modelDef.Id };
        var encoder = new SigLipEncoder(Options.Create(options), NullLogger<SigLipEncoder>.Instance);
        encoder.InitializeFromDisk(activeModelDir, modelDef);

        Assert.True(encoder.IsReady, "SigLipEncoder should be ready after initialization.");

        // 4. Create isolated scratch ZVec collection for ProductImageDoc768
        var schema = ZVecCollectionSchemaBuilder.From<ProductImageDoc768>().Build();
        var collection = new ZVecCollection<ProductImageDoc768>(_factory.OpenOrCreate(_tempCollectionPath + "_img", schema, new ZVecCollectionOptions { EnableMmap = false }));

        var ct = TestContext.Current.CancellationToken;
        try
        {
            // Encode images into ZVec
            var neckVec = encoder.EncodeImage(new MemoryStream(necklaceBytes));
            var shoeVec = encoder.EncodeImage(new MemoryStream(shoeBytes));
            var shirtVec = encoder.EncodeImage(new MemoryStream(shirtBytes));

            await collection.UpsertAsync(new ProductImageDoc768 { Id = "54147_necklace", ImageEmbedding = neckVec }, ct);
            await collection.UpsertAsync(new ProductImageDoc768 { Id = "6671_shoe", ImageEmbedding = shoeVec }, ct);
            await collection.UpsertAsync(new ProductImageDoc768 { Id = "19702_shirt", ImageEmbedding = shirtVec }, ct);

            // --- CROSS-MODAL TEST 1: Text Query "white necklace" against IMAGE collection ---
            var queryNeckVec = encoder.EncodeText("white necklace");
            var hitsNeck = await collection.QueryAsync(p => p.ImageEmbedding, queryNeckVec, topK: 3, ct: ct);

            Assert.NotEmpty(hitsNeck);
            Assert.Equal("54147_necklace", hitsNeck[0].Record.Id);
            _output.WriteLine($"[PASS] Text 'white necklace' -> Top Hit: {hitsNeck[0].Record.Id} (Cosine Dist: {hitsNeck[0].Score:F4})");

            // --- CROSS-MODAL TEST 2: Text Query "running shoes" against IMAGE collection ---
            var queryShoeVec = encoder.EncodeText("running shoes");
            var hitsShoe = await collection.QueryAsync(p => p.ImageEmbedding, queryShoeVec, topK: 3, ct: ct);

            Assert.NotEmpty(hitsShoe);
            Assert.Equal("6671_shoe", hitsShoe[0].Record.Id);
            _output.WriteLine($"[PASS] Text 'running shoes' -> Top Hit: {hitsShoe[0].Record.Id} (Cosine Dist: {hitsShoe[0].Score:F4})");

            // --- CROSS-MODAL TEST 3: Text Query "casual shirt" against IMAGE collection ---
            var queryShirtVec = encoder.EncodeText("casual shirt");
            var hitsShirt = await collection.QueryAsync(p => p.ImageEmbedding, queryShirtVec, topK: 3, ct: ct);

            Assert.NotEmpty(hitsShirt);
            Assert.Equal("19702_shirt", hitsShirt[0].Record.Id);
            _output.WriteLine($"[PASS] Text 'casual shirt' -> Top Hit: {hitsShirt[0].Record.Id} (Cosine Dist: {hitsShirt[0].Score:F4})");
        }
        finally
        {
            collection.Dispose();
        }
    }

    [Fact]
    public async Task ZVec_FTS_Exact_And_Partial_Token_Retrieval()
    {
        // Creates a typed ProductTextDoc768 collection in ZVec with an FTS index on ConcatenatedText
        var schema = ZVecCollectionSchemaBuilder.From<ProductTextDoc768>().Build();
        var collection = new ZVecCollection<ProductTextDoc768>(_factory.OpenOrCreate(_tempCollectionPath + "_fts", schema, new ZVecCollectionOptions { EnableMmap = false }));

        // Create FTS index on ConcatenatedText
        collection.CreateIndex(p => p.ConcatenatedText, new ZVecFtsIndexParam
        {
            Tokenizer = ZVecFtsTokenizer.Standard,
            Filters = [ZVecFtsTokenFilter.Lowercase, ZVecFtsTokenFilter.AsciiFolding]
        });

        var ct = TestContext.Current.CancellationToken;
        try
        {
            var dummyVector = new float[768];
            await collection.UpsertAsync(new ProductTextDoc768
            {
                Id = "54147_necklace",
                ConcatenatedText = "Femella White Necklace · Necklace and Chains · necklace with silver and pearl coloured plastic beads",
                Gender = "Women",
                BaseColour = "White",
                MasterCategory = "Accessories",
                TextEmbedding = dummyVector
            }, ct);

            await collection.UpsertAsync(new ProductTextDoc768
            {
                Id = "6671_shoe",
                ConcatenatedText = "Nike Men's Backboard White Black Shoe · Shoes · casual footwear with rubber sole",
                Gender = "Men",
                BaseColour = "White",
                MasterCategory = "Footwear",
                TextEmbedding = dummyVector
            }, ct);

            // Test 1: Query exact product title with FTS (includeVector: false)
            var ftsQuery = new ZVecQuery
            {
                FieldName = "ConcatenatedText",
                Fts = new ZVecFtsQuery
                {
                    QueryString = "Femella White Necklace",
                    DefaultOperator = ZVecFtsDefaultOperator.Or
                }
            };

            var hits = collection.Untyped.Query(ftsQuery, topk: 2, includeVector: false);
            Assert.NotEmpty(hits);
            Assert.Equal("54147_necklace", hits[0].Id);
            Assert.True(hits[0].Score > 0, "FTS hit score should be positive.");
            _output.WriteLine($"[PASS] FTS query 'Femella White Necklace' matched ID: {hits[0].Id} with score: {hits[0].Score:F4}");

            // Test 2: Query brand keyword (includeVector: false)
            var brandQuery = new ZVecQuery
            {
                FieldName = "ConcatenatedText",
                Fts = new ZVecFtsQuery
                {
                    QueryString = "Nike",
                    DefaultOperator = ZVecFtsDefaultOperator.Or
                }
            };

            var brandHits = collection.Untyped.Query(brandQuery, topk: 2, includeVector: false);
            Assert.NotEmpty(brandHits);
            Assert.Equal("6671_shoe", brandHits[0].Id);
            _output.WriteLine($"[PASS] FTS query 'Nike' matched ID: {brandHits[0].Id} with score: {brandHits[0].Score:F4}");
        }
        finally
        {
            collection.Dispose();
        }
    }

    [Fact]
    public async Task ZVec_Hybrid_Dense_CrossModal_And_FTS_EndToEnd_Retrieval()
    {
        // Tests the full hybrid search pattern: cross-modal visual dense search + FTS keyword ranking
        if (string.IsNullOrEmpty(_modelsDir) || !Directory.Exists(_modelsDir) ||
            string.IsNullOrEmpty(_dataZipPath) || !File.Exists(_dataZipPath))
        {
            Assert.Skip("Models or dataset not present. Skipping test.");
            return;
        }

        var modelDef = SigLipModelCatalog.Get("siglip-base-patch16-224");
        var activeModelDir = Path.Combine(_modelsDir, modelDef.Id);
        if (!File.Exists(Path.Combine(activeModelDir, "vision_model.onnx")) ||
            !File.Exists(Path.Combine(activeModelDir, "text_model.onnx")))
        {
            Assert.Skip($"SigLIP weights missing in {activeModelDir}. Skipping test.");
            return;
        }

        var options = new ProductSearchOptions { ActiveModelId = modelDef.Id };
        var encoder = new SigLipEncoder(Options.Create(options), NullLogger<SigLipEncoder>.Instance);
        encoder.InitializeFromDisk(activeModelDir, modelDef);

        using var zip = ZipFile.OpenRead(_dataZipPath);
        var neckImg = ExtractZipEntry(zip, "images/54147.jpg");
        var shoeImg = ExtractZipEntry(zip, "images/6671.jpg");
        var shirtImg = ExtractZipEntry(zip, "images/19702.jpg");

        var neckVec = encoder.EncodeImage(new MemoryStream(neckImg));
        var shoeVec = encoder.EncodeImage(new MemoryStream(shoeImg));
        var shirtVec = encoder.EncodeImage(new MemoryStream(shirtImg));

        // Create isolated image and text collections
        var imgSchema = ZVecCollectionSchemaBuilder.From<ProductImageDoc768>().Build();
        var txtSchema = ZVecCollectionSchemaBuilder.From<ProductTextDoc768>().Build();

        var imgCol = new ZVecCollection<ProductImageDoc768>(_factory.OpenOrCreate(_tempCollectionPath + "_h_img", imgSchema, new ZVecCollectionOptions { EnableMmap = false }));
        var txtCol = new ZVecCollection<ProductTextDoc768>(_factory.OpenOrCreate(_tempCollectionPath + "_h_txt", txtSchema, new ZVecCollectionOptions { EnableMmap = false }));

        txtCol.CreateIndex(p => p.ConcatenatedText, new ZVecFtsIndexParam
        {
            Tokenizer = ZVecFtsTokenizer.Standard,
            Filters = [ZVecFtsTokenFilter.Lowercase, ZVecFtsTokenFilter.AsciiFolding]
        });

        var ct = TestContext.Current.CancellationToken;
        try
        {
            // Ingest both image vectors and text documents
            await imgCol.UpsertAsync(new ProductImageDoc768 { Id = "54147_necklace", ImageEmbedding = neckVec }, ct);
            await imgCol.UpsertAsync(new ProductImageDoc768 { Id = "6671_shoe", ImageEmbedding = shoeVec }, ct);
            await imgCol.UpsertAsync(new ProductImageDoc768 { Id = "19702_shirt", ImageEmbedding = shirtVec }, ct);

            await txtCol.UpsertAsync(new ProductTextDoc768
            {
                Id = "54147_necklace",
                ConcatenatedText = "Femella White Necklace · Necklace and Chains · necklace with silver and pearl beads",
                Gender = "Women",
                BaseColour = "White",
                MasterCategory = "Accessories",
                TextEmbedding = new float[768]
            }, ct);

            await txtCol.UpsertAsync(new ProductTextDoc768
            {
                Id = "6671_shoe",
                ConcatenatedText = "Nike Men's Backboard White Black Shoe · Shoes · casual footwear with rubber sole",
                Gender = "Men",
                BaseColour = "White",
                MasterCategory = "Footwear",
                TextEmbedding = new float[768]
            }, ct);

            await txtCol.UpsertAsync(new ProductTextDoc768
            {
                Id = "19702_shirt",
                ConcatenatedText = "United Colors Of Benetton Striped Casual Shirt · Tops · casual cotton wear",
                Gender = "Men",
                BaseColour = "Blue",
                MasterCategory = "Apparel",
                TextEmbedding = new float[768]
            }, ct);

            // --- Hybrid Query 1: Exact Product Title "Femella White Necklace" ---
            var queryText = "Femella White Necklace";
            var queryVec = encoder.EncodeText(queryText);

            // 1. Hit Place 1: Dense query against IMAGE collection (visual match)
            var denseHits = await imgCol.QueryAsync(p => p.ImageEmbedding, queryVec, topK: 3, ct: ct);

            // 2. Hit Place 2: In-DB Hybrid query against TEXT collection using native ZVecWeightedReranker
            var textQueries = new List<ZVecQuery>
            {
                new() { FieldName = "TextEmbedding", Vector = queryVec },
                new()
                {
                    FieldName = "ConcatenatedText",
                    Fts = new ZVecFtsQuery { QueryString = queryText, DefaultOperator = ZVecFtsDefaultOperator.Or }
                }
            };
            var reranker = new ZVecWeightedReranker
            {
                TopN = 3,
                Metric = ZVecMetricType.Cosine,
                Weights = new Dictionary<string, float>
                {
                    ["TextEmbedding"] = 0.3f,
                    ["ConcatenatedText"] = 0.7f
                }
            };
            var textHits = txtCol.Untyped.Query(textQueries, topk: 3, reranker: reranker, includeVector: false);
            Assert.NotEmpty(textHits);
            Assert.Equal("54147_necklace", textHits[0].Id);
            _output.WriteLine($"[PASS] Text collection native ZVecWeightedReranker -> Rank 1: {textHits[0].Id} (Score: {textHits[0].Score:F4})");

            // 3. Merge results from both collections (Image + Text)
            var merged = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var h in denseHits) merged[h.Record.Id] = 1f - h.Score; // Cosine similarity
            foreach (var h in textHits)
            {
                if (merged.TryGetValue(h.Id, out var existing))
                    merged[h.Id] = existing + 0.30f; // Dual-match bonus
                else
                    merged[h.Id] = 0.50f; // Exact text match baseline
            }
            var topMerged = merged.OrderByDescending(kv => kv.Value).First();
            Assert.Equal("54147_necklace", topMerged.Key);
            _output.WriteLine($"[PASS] Merged dual-collection result -> Rank 1: {topMerged.Key} (Score: {topMerged.Value:F4})");

            // --- Hybrid Query 2: Pure Visual Query "running shoes" ---
            var shoeQueryVec = encoder.EncodeText("running shoes");
            var shoeDense = await imgCol.QueryAsync(p => p.ImageEmbedding, shoeQueryVec, topK: 3, ct: ct);
            Assert.NotEmpty(shoeDense);
            Assert.Equal("6671_shoe", shoeDense[0].Record.Id);
            _output.WriteLine($"[PASS] Cross-modal query 'running shoes' -> Rank 1: {shoeDense[0].Record.Id} (Cosine Dist: {shoeDense[0].Score:F4})");
        }
        finally
        {
            imgCol.Dispose();
            txtCol.Dispose();
            DeleteTempDir(_tempCollectionPath + "_h_img");
            DeleteTempDir(_tempCollectionPath + "_h_txt");
        }
    }

    [Fact]
    public void SigLip_Demonstrates_Contrastive_Visual_Versus_Text_Geometry()
    {
        // Contrastive dual-encoders (CLIP, SigLIP) are trained on (Image, Text) pairs.
        // As a consequence, Text-to-Image similarity gives sharp, calibrated multimodal separation,
        // while raw Text-to-Text cosine values occupy a collapsed anisotropic cone where unrelated
        // text documents can have high positive similarity (e.g. > 0.50).
        if (string.IsNullOrEmpty(_modelsDir) || !Directory.Exists(_modelsDir))
        {
            Assert.Skip("ProductSearch models directory not found. Skipping test.");
            return;
        }

        var modelDef = SigLipModelCatalog.Get("siglip-base-patch16-224");
        var activeModelDir = Path.Combine(_modelsDir, modelDef.Id);
        if (!File.Exists(Path.Combine(activeModelDir, "text_model.onnx")))
        {
            Assert.Skip($"SigLIP model weights not found in {activeModelDir}. Skipping test.");
            return;
        }

        var options = new ProductSearchOptions { ActiveModelId = modelDef.Id };
        var encoder = new SigLipEncoder(Options.Create(options), NullLogger<SigLipEncoder>.Instance);
        encoder.InitializeFromDisk(activeModelDir, modelDef);

        var query = "white necklace";
        var matchingText = "Femella White Necklace · Necklace and Chains · necklace with beads";
        var unrelatedText = "Puma Women Floral Print Summer Dress · Dress · Clothing · Women";

        var qVec = encoder.EncodeText(query);
        var matchVec = encoder.EncodeText(matchingText);
        var unrelVec = encoder.EncodeText(unrelatedText);

        var simMatch = Dot(qVec, matchVec);
        var simUnrel = Dot(qVec, unrelVec);

        _output.WriteLine($"Query text vs Matching product text cosine:  {simMatch:F4}");
        _output.WriteLine($"Query text vs Unrelated summer dress cosine: {simUnrel:F4}");

        // Both are above 0.50 even though one is a floral dress and one is a necklace
        Assert.True(simUnrel > 0.50f,
            $"Unrelated text cosine {simUnrel:F4} is > 0.50 due to anisotropic text-embedding cone.");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        DeleteTempDir(_tempCollectionPath + "_img");
        DeleteTempDir(_tempCollectionPath + "_fts");
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
        }
    }

    private static byte[] ExtractZipEntry(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName) ?? zip.Entries.First(e => e.Name.Equals(Path.GetFileName(entryName), StringComparison.OrdinalIgnoreCase));
        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        return ms.ToArray();
    }

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
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
