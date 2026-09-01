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

    /// <summary>
    /// Converts raw SigLIP cosine similarity to an intuitive user-facing percentage [0%..100%].
    /// SigLIP cross-modal (text-to-image) true positive cosines lie in [0.03..0.20],
    /// while visual (image-to-image) cosines lie in [0.30..1.00].
    /// </summary>
    public static int SimilarityPercent(float cosine)
    {
        if (cosine <= 0f)
            return 0;

        if (cosine <= 0.25f)
        {
            // Cross-modal text-image range: 0.03 -> ~50%, 0.10 -> ~70%, 0.18 -> ~90%
            var t = Math.Clamp(cosine / 0.20f, 0f, 1f);
            return (int)Math.Round(45f + t * 50f);
        }

        // Visual image-image range: 0.30 -> ~55%, 0.65 -> ~75%, 1.0 -> 100%
        var v = Math.Clamp((cosine - 0.25f) / 0.75f, 0f, 1f);
        return (int)Math.Round(50f + v * 50f);
    }
}
