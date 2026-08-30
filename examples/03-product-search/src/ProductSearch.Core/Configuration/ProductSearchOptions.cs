namespace ProductSearch.Core.Configuration;

/// <summary>
/// App configuration for the SigLIP + ZVec + Postgres product-search demo.
/// Relative paths are resolved against the API ContentRoot in Program.cs PostConfigure.
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
    public string WowQueriesPath { get; set; } = "../../data/wow-queries.json";
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

    /// <summary>
    /// Hugging Face datasets-server rows endpoint. Placeholders: {offset}, {length}.
    /// The server rejects length greater than 100 — we page in 100-row chunks.
    /// </summary>
    public string HuggingFaceRowsUrl { get; set; } =
        "https://datasets-server.huggingface.co/rows?dataset=ashraq/fashion-product-images-small&config=default&split=train&offset={offset}&length={length}";

    /// <summary>datasets-server hard cap; do not raise above 100.</summary>
    public int HuggingFaceRowsPageSize { get; set; } = 100;

    public string HfRowIndexFile { get; set; } = "hf-row-index.tsv";

    public IReadOnlyList<int> AllowedPatchSizes { get; } = [100, 500, 1000];

    /// <summary>Resolve relative demo paths against the API ContentRoot (never process CWD).</summary>
    public static void ResolveRelativePaths(ProductSearchOptions options, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        options.DataRoot = ResolvePath(options.DataRoot, contentRoot);
        options.ModelsDir = ResolvePath(options.ModelsDir, contentRoot);
        options.TextCollectionRoot = ResolvePath(options.TextCollectionRoot, contentRoot);
        options.ImageCollectionRoot = ResolvePath(options.ImageCollectionRoot, contentRoot);
        options.CatalogCachePath = ResolvePath(options.CatalogCachePath, contentRoot);
        options.WowQueriesPath = ResolvePath(options.WowQueriesPath, contentRoot);
    }

    public static string ResolvePath(string path, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, contentRoot);
    }

    public string TextCollectionPathFor(string modelId)
        => Path.Combine(TextCollectionRoot, modelId);

    public string ImageCollectionPathFor(string modelId)
        => Path.Combine(ImageCollectionRoot, modelId);

    public string ModelsDirectoryFor(string modelId)
        => Path.Combine(ModelsDir, modelId);

    public string CatalogStylesPath()
        => Path.Combine(CatalogCachePath, StylesCsvFile);

    public string CatalogRowIndexPath()
        => Path.Combine(CatalogCachePath, HfRowIndexFile);

    public string CatalogImagesDirectory()
        => Path.Combine(CatalogCachePath, ImagesSubdir);
}
