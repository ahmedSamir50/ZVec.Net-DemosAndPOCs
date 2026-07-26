namespace ClipOnnx.App.Encoding;

/// <summary>
/// ZVec Cosine metric exposes a normalized score, not raw CLIP cosθ.
/// Native: distance ∈ [0,2], score ≈ 1 - distance/2 → for unit vectors score ≈ (1 + cosθ) / 2.
/// </summary>
public static class ClipScoreSemantics
{
    /// <summary>Convert ZVec Cosine hit score → CLIP-style cosine in [-1, 1].</summary>
    public static float CosineFromZVecScore(float zvecScore)
        => Math.Clamp(2f * zvecScore - 1f, -1f, 1f);

    /// <summary>Display percent from cosine (negative → 0). Labeled "similarity", not probability.</summary>
    public static int SimilarityPercent(float cosine)
        => cosine <= 0 ? 0 : (int)Math.Round(100.0 * cosine);
}
