using SkiaSharp;

namespace ProductSearch.Core.Encoding;

public static class SigLipImagePreprocessor
{
    private const float Mean = 0.5f;
    private const float Std = 0.5f;

    public static float[] ToSigLipTensor(Stream imageStream, int size)
    {
        using var codec = SKCodec.Create(imageStream)
            ?? throw new InvalidOperationException("Unable to decode image.");
        using var original = SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException("Unable to decode bitmap.");

        using var rgba = original.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Unable to convert image to RGBA.");

        var scale = Math.Max((float)size / rgba.Width, (float)size / rgba.Height);
        var newW = Math.Max(1, (int)Math.Round(rgba.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(rgba.Height * scale));

        using var resized = rgba.Resize(new SKImageInfo(newW, newH), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Resize failed.");

        var left = Math.Max(0, (newW - size) / 2);
        var top = Math.Max(0, (newH - size) / 2);
        var cropW = Math.Min(size, newW);
        var cropH = Math.Min(size, newH);

        using var cropped = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.Clear(SKColors.Black);
            var src = new SKRect(left, top, left + cropW, top + cropH);
            var dst = new SKRect(0, 0, cropW, cropH);
            canvas.DrawBitmap(resized, src, dst);
        }

        var tensor = new float[3 * size * size];
        var pixels = cropped.Pixels;
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

    public static float[] ToSigLipTensor(string filePath, int size)
    {
        using var fs = File.OpenRead(filePath);
        return ToSigLipTensor(fs, size);
    }
}
