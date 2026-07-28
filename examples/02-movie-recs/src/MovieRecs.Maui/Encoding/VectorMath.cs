namespace MovieRecs.Maui.Encoding;

/// <summary>Small vector helpers used after MiniLM encode and for user behaviour vectors.</summary>
internal static class VectorMath
{
    /// <summary>
    /// L2-normalize: <c>v := v / ||v||₂</c>.
    /// MiniLM / sentence-transformers embeddings are typically unit-length; with ZVec Cosine
    /// metric, unit vectors keep scores comparable (cosine ≈ dot product).
    /// Near-zero vectors are returned unchanged to avoid NaN.
    /// </summary>
    public static float[] L2Normalize(ReadOnlySpan<float> v)
    {
        double sumSq = 0;
        for (var i = 0; i < v.Length; i++)
            sumSq += v[i] * (double)v[i];

        var norm = Math.Sqrt(sumSq);
        if (norm < 1e-12)
            return v.ToArray();

        var result = new float[v.Length];
        var inv = 1.0 / norm;
        for (var i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] * inv);
        return result;
    }

    /// <summary>
    /// Mean of several same-dim vectors, then L2 — the demo “user behaviour vector”
    /// (average of liked movie embeddings in the same space as indexed items).
    /// </summary>
    public static float[] AverageThenL2Normalize(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
            throw new ArgumentException("At least one vector is required.", nameof(vectors));
        if (vectors.Count == 1)
            return L2Normalize(vectors[0]);

        var dim = vectors[0].Length;
        var acc = new double[dim];
        foreach (var v in vectors)
        {
            if (v.Length != dim)
                throw new ArgumentException("All vectors must share the same dimension.");
            for (var i = 0; i < dim; i++)
                acc[i] += v[i];
        }

        var mean = new float[dim];
        var invN = 1.0 / vectors.Count;
        for (var i = 0; i < dim; i++)
            mean[i] = (float)(acc[i] * invN);
        return L2Normalize(mean);
    }

    /// <summary>
    /// ZVec Cosine metric exposes <b>cosine distance</b> on hit <c>Score</c>, not raw cosθ.
    /// Official relation: distance ≈ 1 − cosθ (0 = identical, 1 ≈ orthogonal, 2 = opposite).
    /// UI percent = round(100 × cosθ); not a calibrated probability.
    /// </summary>
    public static (double Cosine, int Percent) FromZVecDistance(float distance)
    {
        var cosine = 1.0 - distance;
        var percent = (int)Math.Max(0, Math.Round(100.0 * cosine));
        return (cosine, percent);
    }

    /// <summary>Dot product of two same-dim vectors (unit vectors → cosθ).</summary>
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vector lengths must match.");
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (double)b[i];
        return sum;
    }
}
