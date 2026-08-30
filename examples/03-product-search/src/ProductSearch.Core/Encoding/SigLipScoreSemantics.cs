namespace ProductSearch.Core.Encoding;

public static class SigLipScoreSemantics
{
    public static float CosineFromZVecScore(float zvecDistance)
        => Math.Clamp(1f - zvecDistance, -1f, 1f);

    public static int SimilarityPercent(float cosine)
        => cosine <= 0 ? 0 : (int)Math.Round(100.0 * cosine);
}
