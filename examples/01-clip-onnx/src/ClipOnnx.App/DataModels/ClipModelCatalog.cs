namespace ClipOnnx.App.DataModels;

public static class ClipModelCatalog
{
    public const string EncodePipelineVersion = "clip-onnx-v1";

    public const string DefaultModelId = "clip-vit-b16";

    private const string Inference4jB32 =
        "https://huggingface.co/inference4j/clip-vit-base-patch32/resolve/main";

    private const string XenovaB16 =
        "https://huggingface.co/Xenova/clip-vit-base-patch16/resolve/main";

    private const string XenovaL14 =
        "https://huggingface.co/Xenova/clip-vit-large-patch14/resolve/main";

    public static IReadOnlyList<ClipModelDefinition> All { get; } =
    [
        new(
            Id: "clip-vit-b32",
            DisplayName: "OpenAI CLIP ViT-B/32",
            HfRepo: "inference4j/clip-vit-base-patch32",
            EmbeddingDim: 512,
            AccuracyTier: "Low",
            LatencyExpectation: "Fastest on CPU",
            DownloadSizeNote: "~600 MB ONNX total",
            VramNote: "FP32 CPU demo — no GPU required.",
            WhenToPick: "Smoke tests and fastest encode; weakest retrieval quality.",
            RemoteFiles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision_model.onnx"] = $"{Inference4jB32}/vision_model.onnx",
                ["text_model.onnx"] = $"{Inference4jB32}/text_model.onnx",
                ["vocab.json"] = $"{Inference4jB32}/vocab.json",
                ["merges.txt"] = $"{Inference4jB32}/merges.txt"
            }),
        new(
            Id: "clip-vit-b16",
            DisplayName: "OpenAI CLIP ViT-B/16",
            HfRepo: "Xenova/clip-vit-base-patch16",
            EmbeddingDim: 512,
            AccuracyTier: "Balanced",
            LatencyExpectation: "Moderate on CPU",
            DownloadSizeNote: "~600 MB ONNX total",
            VramNote: "FP32 CPU demo — no GPU required.",
            WhenToPick: "Default demo model — balanced speed and quality.",
            RemoteFiles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision_model.onnx"] = $"{XenovaB16}/onnx/vision_model.onnx",
                ["text_model.onnx"] = $"{XenovaB16}/onnx/text_model.onnx",
                ["vocab.json"] = $"{XenovaB16}/vocab.json",
                ["merges.txt"] = $"{XenovaB16}/merges.txt"
            }),
        new(
            Id: "clip-vit-l14",
            DisplayName: "OpenAI CLIP ViT-L/14",
            HfRepo: "Xenova/clip-vit-large-patch14",
            EmbeddingDim: 768,
            AccuracyTier: "High",
            LatencyExpectation: "Slow on CPU — pre-ingest before talks",
            DownloadSizeNote: "~1.7 GB vision ONNX + text encoder",
            VramNote: "Large FP32 graphs; 4 GB VRAM not assumed for CUDA.",
            WhenToPick: "Best retrieval quality — slower encode and larger download.",
            RemoteFiles: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision_model.onnx"] = $"{XenovaL14}/onnx/vision_model.onnx",
                ["text_model.onnx"] = $"{XenovaL14}/onnx/text_model.onnx",
                ["vocab.json"] = $"{XenovaL14}/vocab.json",
                ["merges.txt"] = $"{XenovaL14}/merges.txt"
            })
    ];

    public static ClipModelDefinition Get(string modelId)
    {
        var hit = All.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ArgumentException($"Unknown CLIP model id '{modelId}'.", nameof(modelId));
    }

    public static string DownloadUrl(ClipModelDefinition model, string remote)
    {
        if (remote.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || remote.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return remote;

        return $"https://huggingface.co/{model.HfRepo}/resolve/main/{remote.TrimStart('/')}";
    }
}
