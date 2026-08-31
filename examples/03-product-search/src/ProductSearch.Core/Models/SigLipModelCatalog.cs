namespace ProductSearch.Core.Models;

public sealed record SigLipModelDefinition(
    string Id,
    string DisplayName,
    string HfRepo,
    int EmbeddingDim,
    int ImageSize,
    int PatchSize,
    string AccuracyTier,
    string LatencyExpectation,
    string DownloadSizeNote,
    string WhenToPick,
    IReadOnlyList<string> RequiredModelFiles,
    string SentencePieceFile,
    bool LowercaseText,
    bool UseBilinearResize,
    IReadOnlyDictionary<string, string> RemoteFiles,
    IReadOnlyDictionary<string, long> ExpectedFileBytes);

public static class SigLipModelCatalog
{
    public const string EncodePipelineVersion = "siglip-onnx-v2";

    public const string DefaultModelId = "siglip-base-patch16-224";

    /// <summary>Higher-quality model users can opt into from Status (not bootstrapped by default).</summary>
    public const string RecommendedModelId = "siglip2-so400m-patch14-384";

    private const string XenovaBase = "https://huggingface.co/Xenova/siglip-base-patch16-224/resolve/main";
    private const string OnnxCommunitySigLip2 =
        "https://huggingface.co/onnx-community/siglip2-so400m-patch14-384-ONNX/resolve/main";

    /// <summary>Exact byte sizes for Xenova SigLIP 1 base (small/medium model).</summary>
    private static readonly IReadOnlyDictionary<string, long> BaseExpectedFileBytes =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["vision_model.onnx"] = 371_819_850,
            ["text_model.onnx"] = 441_332_132,
            ["tokenizer.json"] = 2_398_744,
            ["tokenizer_config.json"] = 739,
            ["spiece.model"] = 798_330
        };

    /// <summary>Exact byte sizes for onnx-community SigLIP 2 SO400M split ONNX.</summary>
    private static readonly IReadOnlyDictionary<string, long> SigLip2ExpectedFileBytes =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["vision_model.onnx"] = 1_713_485_119,
            ["text_model.onnx"] = 599_026,
            ["text_model.onnx_data"] = 2_831_131_584,
            ["tokenizer.json"] = 34_363_039,
            ["tokenizer.model"] = 4_241_003,
            ["tokenizer_config.json"] = 47_240
        };

    private static readonly string[] BaseRequiredFiles =
    [
        "vision_model.onnx",
        "text_model.onnx",
        "tokenizer.json",
        "spiece.model"
    ];

    private static readonly string[] SigLip2RequiredFiles =
    [
        "vision_model.onnx",
        "text_model.onnx",
        "text_model.onnx_data",
        "tokenizer.json",
        "tokenizer.model"
    ];

    public static IReadOnlyList<SigLipModelDefinition> All { get; } =
    [
        new(
            Id: "siglip-base-patch16-224",
            DisplayName: "SigLIP Base patch16 224",
            HfRepo: "Xenova/siglip-base-patch16-224",
            EmbeddingDim: 768,
            ImageSize: 224,
            PatchSize: 16,
            AccuracyTier: "Balanced",
            LatencyExpectation: "Fast on CPU",
            DownloadSizeNote: "~800 MB ONNX total",
            WhenToPick: "Default demo model — good speed/quality trade-off.",
            RequiredModelFiles: BaseRequiredFiles,
            SentencePieceFile: "spiece.model",
            LowercaseText: true,
            UseBilinearResize: false,
            RemoteFiles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision_model.onnx"] = $"{XenovaBase}/onnx/vision_model.onnx",
                ["text_model.onnx"] = $"{XenovaBase}/onnx/text_model.onnx",
                ["tokenizer.json"] = $"{XenovaBase}/tokenizer.json",
                ["tokenizer_config.json"] = $"{XenovaBase}/tokenizer_config.json",
                ["spiece.model"] = $"{XenovaBase}/spiece.model"
            },
            ExpectedFileBytes: BaseExpectedFileBytes),
        new(
            Id: "siglip2-so400m-patch14-384",
            DisplayName: "SigLIP 2 SO400M patch14 384",
            HfRepo: "google/siglip2-so400m-patch14-384",
            EmbeddingDim: 1152,
            ImageSize: 384,
            PatchSize: 14,
            AccuracyTier: "High",
            LatencyExpectation: "Slower encode; pre-ingest before talks",
            DownloadSizeNote: "~4.5 GB ONNX total (vision + text external data)",
            WhenToPick: "Recommended for best retrieval quality — slower encode and ~4.5 GB download.",
            RequiredModelFiles: SigLip2RequiredFiles,
            SentencePieceFile: "tokenizer.model",
            LowercaseText: false,
            UseBilinearResize: true,
            RemoteFiles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision_model.onnx"] = $"{OnnxCommunitySigLip2}/onnx/vision_model.onnx",
                ["text_model.onnx"] = $"{OnnxCommunitySigLip2}/onnx/text_model.onnx",
                ["text_model.onnx_data"] = $"{OnnxCommunitySigLip2}/onnx/text_model.onnx_data",
                ["tokenizer.json"] = $"{OnnxCommunitySigLip2}/tokenizer.json",
                ["tokenizer.model"] = $"{OnnxCommunitySigLip2}/tokenizer.model",
                ["tokenizer_config.json"] = $"{OnnxCommunitySigLip2}/tokenizer_config.json"
            },
            ExpectedFileBytes: SigLip2ExpectedFileBytes)
    ];

    public static SigLipModelDefinition Get(string modelId)
    {
        if (string.Equals(modelId, "siglip-so400m-patch14-384", StringComparison.OrdinalIgnoreCase))
            modelId = "siglip2-so400m-patch14-384";

        var hit = All.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ArgumentException($"Unknown SigLIP model id '{modelId}'.", nameof(modelId));
    }

    public static string DownloadUrl(SigLipModelDefinition model, string localFileName)
    {
        if (!model.RemoteFiles.TryGetValue(localFileName, out var url))
            throw new InvalidOperationException($"Catalog missing remote path for {localFileName} on {model.Id}.");
        return url;
    }

    public static bool TryGetExpectedBytes(SigLipModelDefinition model, string localFileName, out long expectedBytes)
        => model.ExpectedFileBytes.TryGetValue(localFileName, out expectedBytes);
}
