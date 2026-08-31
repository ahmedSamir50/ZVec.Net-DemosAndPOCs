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

    /// <summary>In-repo curated pack (10k SKUs). Resolved against API ContentRoot.</summary>
    public string CatalogPackZip { get; set; } = "../../data/fashion-10k.zip";

    public string StylesCsvFile { get; set; } = "data.csv";
    public string ImagesSubdir { get; set; } = "images";

    public string VisionModelFile { get; set; } = "vision_model.onnx";
    public string TextModelFile { get; set; } = "text_model.onnx";
    public string TokenizerFile { get; set; } = "tokenizer.json";
    public string TokenizerConfigFile { get; set; } = "tokenizer_config.json";

    public bool AutoDownloadModels { get; set; } = true;

    public int DefaultTopK { get; set; } = 10;
    public int DefaultPatchSize { get; set; } = 100;
  public int IngestChunkSize { get; set; } = 20;
    public long MaxUploadBytes { get; set; } = 8 * 1024 * 1024;

    public float MinCosine { get; set; } = 0.15f;
    public float MaxCosineGapFromTop { get; set; } = 0.12f;
    public int MinConfidentHits { get; set; } = 1;

    public float DenseFusionWeight { get; set; } = 0.7f;
    public float FtsFusionWeight { get; set; } = 0.3f;
    public float TextCollectionFusionWeight { get; set; } = 0.5f;
    public float ImageCollectionFusionWeight { get; set; } = 0.5f;

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
        options.CatalogPackZip = ResolvePath(options.CatalogPackZip, contentRoot);
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

    public string CatalogCsvPath()
        => Path.Combine(CatalogCachePath, StylesCsvFile);

    public string CatalogImagesDirectory()
        => Path.Combine(CatalogCachePath, ImagesSubdir);
}
