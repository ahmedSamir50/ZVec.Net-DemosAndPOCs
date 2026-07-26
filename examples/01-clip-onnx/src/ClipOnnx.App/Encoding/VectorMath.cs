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
    /// Output length equals input length (512 for CLIP ViT-B/32).
    /// </summary>
    public static float[] L2Normalize(ReadOnlySpan<float> v)
    {
        // ||v||² in double for better sum accuracy on 512 floats
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
}
