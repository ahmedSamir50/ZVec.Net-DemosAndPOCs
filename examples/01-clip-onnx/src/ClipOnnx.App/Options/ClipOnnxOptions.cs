namespace ClipOnnx.App.Options;

/// <summary>
/// App configuration for the CLIP ONNX + ZVec gallery demo.
/// Multi-model: set <see cref="ActiveModelId"/> to one of the catalog ids (B/32, B/16, L/14).
/// ONNX files live under <c>{ModelsDir}/{modelId}/</c> so downloads never overwrite each other.
/// </summary>
public sealed class ClipOnnxOptions
{
    public const string SectionName = "ClipOnnx";

    /// <summary>Root for images, manifests, zvec store, model files.</summary>
    public string DataRoot { get; set; } = "./data";

    /// <summary>Parent folder for per-model ONNX dirs (e.g. ./models/clip-vit-b16/).</summary>
    public string ModelsDir { get; set; } = "./models";

    /// <summary>
    /// Active CLIP dual-encoder id from <see cref="Models.ClipModelCatalog"/>:
    /// <c>clip-vit-b32</c>, <c>clip-vit-b16</c> (default), or <c>clip-vit-l14</c>.
    /// </summary>
    public string ActiveModelId { get; set; } = "clip-vit-b16";

    public string VisionModelFile { get; set; } = "vision_model.onnx";
    public string TextModelFile { get; set; } = "text_model.onnx";
    public string VocabFile { get; set; } = "vocab.json";
    public string MergesFile { get; set; } = "merges.txt";

    /// <summary>When true, missing model files are downloaded from Hugging Face on startup / select.</summary>
    public bool AutoDownloadModels { get; set; } = true;

    /// <summary>Parent path for per-model ZVec collections: {CollectionPath}/{modelId}/.</summary>
    public string CollectionPath { get; set; } = "./data/zvec-clip-gallery";
    public bool EnableMmap { get; set; } = true;

    /// <summary>
    /// Max images to encode+upsert per ingest run (resume from saved offset).
    /// Does not partial-download the Flickr zip — the image archive is fetched once in full when needed.
    /// </summary>
    public int DefaultBatchSize { get; set; } = 100;
    public int DefaultTopK { get; set; } = 10;
    public long MaxUploadBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Flickr8k image zip (jbrownlee mirror).</summary>
    public string FlickrImagesZipUrl { get; set; } =
        "https://github.com/jbrownlee/Datasets/releases/download/Flickr8k/Flickr8k_Dataset.zip";

    /// <summary>Flickr8k text zip containing train/test/dev image lists + captions.</summary>
    public string FlickrTextZipUrl { get; set; } =
        "https://github.com/jbrownlee/Datasets/releases/download/Flickr8k/Flickr8k_text.zip";

    /// <summary>Manifest filename inside the text zip / data/flickr8k/.</summary>
    public string FlickrManifestFile { get; set; } = "Flickr_8k.trainImages.txt";

    /// <summary>Caption token file (filename#i caption) — UI enrichment only, not indexed.</summary>
    public string FlickrCaptionsFile { get; set; } = "Flickr8k.token.txt";

    /// <summary>
    /// Text search prompt template. Use <c>{query}</c> placeholder.
    /// Used when <see cref="TextPromptTemplates"/> is empty.
    /// </summary>
    public string TextPromptTemplate { get; set; } = "a photo of {query}";

    /// <summary>
    /// Multi-prompt ensemble for text search. Default: single OpenAI-style wrap (sharper than 3-way mean).
    /// </summary>
    public string[] TextPromptTemplates { get; set; } =
    [
        "a photo of {query}"
    ];

    /// <summary>Minimum CLIP cosine (after converting ZVec distance) to keep a hit. Default 0.20.</summary>
    public float MinCosine { get; set; } = 0.20f;

    /// <summary>Drop hits whose cosine is more than this below the top-1 cosine. Default 0.12.</summary>
    public float MaxCosineGapFromTop { get; set; } = 0.12f;

    /// <summary>
    /// If fewer than this many hits survive min+gap filters, return empty.
    /// Default 1 — show sparse true-CLIP matches (e.g. one watermelon hit); do not wipe 1–2 good hits.
    /// </summary>
    public int MinConfidentHits { get; set; } = 1;

    /// <summary>Local filenames expected inside each model directory.</summary>
    public IReadOnlyList<string> RequiredModelFiles =>
    [
        VisionModelFile,
        TextModelFile,
        VocabFile,
        MergesFile
    ];
}
