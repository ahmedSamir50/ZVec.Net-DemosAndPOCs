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
    /// Bump when encode path changes so old indexes are treated as mismatch.
    /// v3 = refuse BERT pooler_output; mean-pool last_hidden_state / sentence_embedding.
    /// </summary>
    public const string EncodePipelineVersion = "minilm-meanpool-l2-v3-seq256";

    public const string OnnxAssetPath = "models/all-MiniLM-L6-v2.onnx";
    public const string VocabAssetPath = "models/vocab.txt";
    public const string MoviesAssetPath = "movielens/movies.csv";
    public const string RatingsAssetPath = "movielens/ratings.csv";

    /// <summary>Max sequence length for MiniLM — sentence-transformers default (256) for demo quality.</summary>
    public int MaxSequenceLength { get; set; } = 256;

    public int DefaultTopK { get; set; } = 12;

    /// <summary>MovieLens rating threshold treated as a “like” for user behaviour vectors.</summary>
    public float MinLikeRating { get; set; } = 4.0f;

    /// <summary>Minimum raw cosine for a confident neighbor (gates applied before genre/franchise bonuses).</summary>
    public float MinCosine { get; set; } = 0.25f;

    /// <summary>Keep hits within this cosine of the best remaining raw cosine.</summary>
    public float MaxCosineGapFromTop { get; set; } = 0.12f;

    /// <summary>ANN over-fetch before rerank / inject.</summary>
    public int RecommendFetch { get; set; } = 120;

    public float GenreJaccardBonusCap { get; set; } = 0.12f;
    public float FranchiseBonus { get; set; } = 0.20f;
    public int MaxFranchiseInjects { get; set; } = 5;
}
