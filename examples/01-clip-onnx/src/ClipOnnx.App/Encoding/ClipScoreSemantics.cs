namespace ClipOnnx.App.Encoding;

/// <summary>
/// ZVec Cosine metric exposes <b>cosine distance</b> on hit <c>Score</c>, not raw CLIP cosθ.
/// Official Zvec: Cosine Distance ≈ 1 − cosθ (lower distance = more similar).
/// Typical range ≈ [0, 2] for unit vectors (0 = identical, 1 = orthogonal, 2 = opposite).
/// </summary>
public static class ClipScoreSemantics
{
    /// <summary>
    /// Convert ZVec Cosine hit score (distance) → CLIP-style cosine in [-1, 1].
    /// <c>cosθ = 1 − distance</c>.
    /// </summary>
    public static float CosineFromZVecScore(float zvecDistance)
        => Math.Clamp(1f - zvecDistance, -1f, 1f);

    /// <summary>Display percent from cosine (negative → 0). Higher % = better match. Not a calibrated probability.</summary>
    public static int SimilarityPercent(float cosine)
        => cosine <= 0 ? 0 : (int)Math.Round(100.0 * cosine);
}
