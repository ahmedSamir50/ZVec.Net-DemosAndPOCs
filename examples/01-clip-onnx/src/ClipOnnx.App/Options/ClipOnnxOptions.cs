namespace ClipOnnx.App.Options;

public sealed class ClipOnnxOptions
{
    public const string SectionName = "ClipOnnx";

    /// <summary>Root for images, manifests, zvec store, model files.</summary>
    public string DataRoot { get; set; } = "./data";

    public string ModelsDir { get; set; } = "./models";

    public string VisionModelFile { get; set; } = "vision_model.onnx";
    public string TextModelFile { get; set; } = "text_model.onnx";
    public string VocabFile { get; set; } = "vocab.json";
    public string MergesFile { get; set; } = "merges.txt";

    /// <summary>When true, missing model files are downloaded from Hugging Face on startup.</summary>
    public bool AutoDownloadModels { get; set; } = true;

    /// <summary>Hugging Face repo id (documentation / logging).</summary>
    public string ModelRepo { get; set; } = "inference4j/clip-vit-base-patch32";

    /// <summary>
    /// URL template with {file} placeholder.
    /// Default: https://huggingface.co/{repo}/resolve/main/{file}
    /// </summary>
    public string ModelDownloadUrlTemplate { get; set; } =
        "https://huggingface.co/inference4j/clip-vit-base-patch32/resolve/main/{file}";

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

    /// <summary>Flickr8k text zip containing train/test/dev image lists.</summary>
    public string FlickrTextZipUrl { get; set; } =
        "https://github.com/jbrownlee/Datasets/releases/download/Flickr8k/Flickr8k_text.zip";

    /// <summary>Manifest filename inside the text zip / data/flickr8k/.</summary>
    public string FlickrManifestFile { get; set; } = "Flickr_8k.trainImages.txt";

    public IReadOnlyList<string> RequiredModelFiles =>
    [
        VisionModelFile,
        TextModelFile,
        VocabFile,
        MergesFile
    ];
}
