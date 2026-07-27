namespace MovieRecs.Maui.Options;

/// <summary>Demo constants for MiniLM + MovieLens + ZVec pipeline identity.</summary>
public sealed class MovieRecsOptions
{
    public const string SectionName = "MovieRecs";

    /// <summary>Stable id stored in the index stamp — must match between ingest and search.</summary>
    public const string ModelId = "all-minilm-l6-v2";

    /// <summary>MiniLM output dim; must match <c>[ZVecVector(384)]</c> on <see cref="Models.Movie"/>.</summary>
    public const int EmbeddingDim = 384;

    /// <summary>
    /// Bump when encode path changes (e.g. seq length 128→256) so old indexes are treated as mismatch.
    /// </summary>
    public const string EncodePipelineVersion = "minilm-meanpool-l2-v2-seq256";

    public const string OnnxAssetPath = "models/all-MiniLM-L6-v2.onnx";
    public const string VocabAssetPath = "models/vocab.txt";
    public const string MoviesAssetPath = "movielens/movies.csv";
    public const string RatingsAssetPath = "movielens/ratings.csv";

    /// <summary>Max sequence length for MiniLM — sentence-transformers default (256) for demo quality.</summary>
    public int MaxSequenceLength { get; set; } = 256;

    public int DefaultTopK { get; set; } = 12;

    /// <summary>MovieLens rating threshold treated as a “like” for user behaviour vectors.</summary>
    public float MinLikeRating { get; set; } = 4.0f;
}
