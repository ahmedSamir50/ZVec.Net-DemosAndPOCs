namespace ProductSearch.Core.Encoding;

/// <summary>
/// ZVec Cosine metric exposes <b>cosine distance</b> on hit Score (lower = closer).
/// Relation: cosθ ≈ 1 − distance for unit vectors.
/// </summary>
public static class SigLipScoreSemantics
{
    /// <summary>Cosine from pgvector / ZVec cosine <b>distance</b> (lower distance = closer).</summary>
    public static float CosineFromDistance(float distance)
        => Math.Clamp(1f - distance, -1f, 1f);

    public static int SimilarityPercent(float cosine)
        => cosine <= 0 ? 0 : (int)Math.Round(100.0 * cosine);
}
