using SkiaSharp;

namespace ProductSearch.Core.Encoding;

/// <summary>
/// SigLIP image preprocess: stretch-resize to H×W (no center crop), mean/std 0.5.
/// </summary>
public static class SigLipImagePreprocessor
{
    private const float Mean = 0.5f;
    private const float Std = 0.5f;

    /// <param name="imageStream">Decoded image bytes.</param>
    /// <param name="size">Target width and height (stretch resize).</param>
    /// <param name="bilinear">
    /// When true, use bilinear (SigLIP 2 HF resample=2). When false, use Mitchell cubic (SigLIP 1 bicubic).
    /// </param>
    public static float[] ToSigLipTensor(Stream imageStream, int size, bool bilinear = false)
    {
        using var codec = SKCodec.Create(imageStream)
            ?? throw new InvalidOperationException("Unable to decode image.");
        using var original = SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException("Unable to decode bitmap.");

        using var rgba = original.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Unable to convert image to RGBA.");

        var sampling = bilinear
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);

        using var resized = rgba.Resize(new SKImageInfo(size, size), sampling)
            ?? throw new InvalidOperationException("Resize failed.");

        var tensor = new float[3 * size * size];
        var pixels = resized.Pixels;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var c = pixels[y * size + x];
                var idx = y * size + x;
                tensor[0 * size * size + idx] = ((c.Red / 255f) - Mean) / Std;
                tensor[1 * size * size + idx] = ((c.Green / 255f) - Mean) / Std;
                tensor[2 * size * size + idx] = ((c.Blue / 255f) - Mean) / Std;
            }
        }

        return tensor;
    }

    public static float[] ToSigLipTensor(string filePath, int size, bool bilinear = false)
    {
        using var fs = File.OpenRead(filePath);
        return ToSigLipTensor(fs, size, bilinear);
    }
}
