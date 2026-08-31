namespace ProductSearch.Core.Encoding;

internal static class VectorMath
{
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

    /// <summary>Dot product for L2-normalized vectors (= cosine similarity).</summary>
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
