namespace ProductSearch.Core.Configuration;

/// <summary>
/// App configuration for the SigLIP + ZVec + Postgres product-search demo.
/// </summary>
public sealed class ProductSearchOptions
{
    public const string SectionName = "ProductSearch";

    public string DataRoot { get; set; } = "./data";
    public string ModelsDir { get; set; } = "./models";
    public string ActiveModelId { get; set; } = "siglip-base-patch16-224";

    public string TextCollectionRoot { get; set; } = "./data/zvec-text";
    public string ImageCollectionRoot { get; set; } = "./data/zvec-image";
    public bool EnableMmap { get; set; } = true;

    public string PostgresConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=productsearch;Username=postgres;Password=postgres";

    public string CatalogCachePath { get; set; } = "./data/cache/fashion-small";
    public string WowQueriesPath { get; set; } = "./data/wow-queries.json";
    public string StylesCsvFile { get; set; } = "styles.csv";
    public string ImagesSubdir { get; set; } = "images";

    public string VisionModelFile { get; set; } = "vision_model.onnx";
    public string TextModelFile { get; set; } = "text_model.onnx";
    public string TokenizerFile { get; set; } = "tokenizer.json";
    public string TokenizerConfigFile { get; set; } = "tokenizer_config.json";

    public bool AutoDownloadModels { get; set; } = true;

    public int DefaultTopK { get; set; } = 10;
    public int DefaultPatchSize { get; set; } = 100;
    public long MaxUploadBytes { get; set; } = 8 * 1024 * 1024;

    public float MinCosine { get; set; } = 0.15f;
    public float MaxCosineGapFromTop { get; set; } = 0.12f;
    public int MinConfidentHits { get; set; } = 1;

    public float DenseFusionWeight { get; set; } = 0.7f;
    public float FtsFusionWeight { get; set; } = 0.3f;
    public float TextCollectionFusionWeight { get; set; } = 0.5f;
    public float ImageCollectionFusionWeight { get; set; } = 0.5f;

    public string StylesCsvUrl { get; set; } =
        "https://huggingface.co/datasets/ashraq/fashion-product-images-small/resolve/main/styles.csv";

    public string ImageUrlTemplate { get; set; } =
        "https://huggingface.co/datasets/ashraq/fashion-product-images-small/resolve/main/images/{id}.jpg";

    public IReadOnlyList<int> AllowedPatchSizes { get; } = [100, 500, 1000];

    public string TextCollectionPathFor(string modelId)
        => Path.GetFullPath(Path.Combine(TextCollectionRoot, modelId));

    public string ImageCollectionPathFor(string modelId)
        => Path.GetFullPath(Path.Combine(ImageCollectionRoot, modelId));

    public string ModelsDirectoryFor(string modelId)
        => Path.GetFullPath(Path.Combine(ModelsDir, modelId));

    public string CatalogStylesPath()
        => Path.GetFullPath(Path.Combine(CatalogCachePath, StylesCsvFile));

    public string CatalogImagesDirectory()
        => Path.GetFullPath(Path.Combine(CatalogCachePath, ImagesSubdir));
}
