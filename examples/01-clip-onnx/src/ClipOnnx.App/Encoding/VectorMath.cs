namespace ClipOnnx.App.Encoding;

/// <summary>
/// Tiny vector helpers used after ONNX encode.
/// </summary>
internal static class VectorMath
{
    /// <summary>
    /// L2-normalize: v := v / ||v||₂  where  ||v||₂ = sqrt(Σ vᵢ²).
    ///
    /// Why here?
    ///   CLIP embeddings are typically unit-length. For unit vectors,
    ///   cosine(a,b) = a·b  (dot product). We store Cosine metric in ZVec;
    ///   normalizing both index and query keeps scores comparable and stable.
    ///
    /// Near-zero vectors (norm &lt; 1e-12) are returned unchanged to avoid NaN.
    /// Output length equals input length (512 for B/32·B/16, 768 for L/14).
    /// </summary>
    public static float[] L2Normalize(ReadOnlySpan<float> v)
    {
        // ||v||² in double for better sum accuracy on hundreds of floats
        double sumSq = 0;
        for (var i = 0; i < v.Length; i++)
            sumSq += v[i] * (double)v[i];

        var norm = Math.Sqrt(sumSq);
        if (norm < 1e-12)
            return v.ToArray();

        var result = new float[v.Length];
        var inv = 1.0 / norm; // multiply is cheaper than divide in the loop
        for (var i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] * inv);
        return result;
    }

    /// <summary>Element-wise mean of several same-length vectors, then L2-normalize (prompt ensemble).</summary>
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
}
