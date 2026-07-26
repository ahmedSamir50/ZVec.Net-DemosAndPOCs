using SkiaSharp;

namespace ClipOnnx.App.Encoding;

/// <summary>
/// Turns a photo into the float tensor CLIP ViT-B/32 expects.
///
/// Contract for the vision ONNX input:
///   shape  [1, 3, 224, 224]  (batch, RGB channels, H, W) — NCHW
///   dtype  float32
///   values already mean/std-normalized (see Mean/Std below)
///
/// Why 224? ViT-B/32 was trained on 224×224 patches (patch size 32 → 7×7 tokens).
///
/// Preprocess matches OpenAI / HF CLIP:
///   scale so the short side is 224, then CENTER-CROP 224×224 (edges discarded).
/// </summary>
public static class ClipImagePreprocessor
{
    /// <summary>CLIP ViT-B/32 spatial input size (pixels). Must match the ONNX graph.</summary>
    public const int Size = 224;

    /// <summary>
    /// OpenAI CLIP RGB channel means (dataset stats used at training time).
    /// Applied as: (pixel_01 - mean) / std  per channel.
    /// </summary>
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];

    /// <summary>OpenAI CLIP RGB channel stddevs (paired with <see cref="Mean"/>).</summary>
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>
    /// Decode → short-side resize to 224 → center-crop 224×224 → NCHW float plane
    /// of length 3*224*224 (caller wraps with batch dim = 1 for ONNX).
    /// </summary>
    public static float[] ToClipTensor(Stream imageStream)
    {
        using var codec = SKCodec.Create(imageStream)
            ?? throw new InvalidOperationException("Unable to decode image.");
        using var original = SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException("Unable to decode bitmap.");

        using var rgba = original.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Unable to convert image to RGBA.");

        // Short-side scale to Size (CLIP / torchvision Resize then CenterCrop).
        var scale = Math.Max((float)Size / rgba.Width, (float)Size / rgba.Height);
        var newW = Math.Max(1, (int)Math.Round(rgba.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(rgba.Height * scale));

        // Bicubic-family resample (CLIP training used bicubic).
        using var resized = rgba.Resize(new SKImageInfo(newW, newH), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Resize failed.");

        var left = Math.Max(0, (newW - Size) / 2);
        var top = Math.Max(0, (newH - Size) / 2);
        var cropW = Math.Min(Size, newW);
        var cropH = Math.Min(Size, newH);

        using var cropped = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.Clear(SKColors.Black);
            var src = new SKRect(left, top, left + cropW, top + cropH);
            var dst = new SKRect(0, 0, cropW, cropH);
            canvas.DrawBitmap(resized, src, dst);
        }

        // Flatten to NCHW without batch: R plane, then G, then B (each Size*Size).
        var tensor = new float[3 * Size * Size];
        var pixels = cropped.Pixels;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var c = pixels[y * Size + x];
                var idx = y * Size + x;
                // Scale 0–255 → 0–1, then CLIP normalize per channel.
                tensor[0 * Size * Size + idx] = ((c.Red / 255f) - Mean[0]) / Std[0];
                tensor[1 * Size * Size + idx] = ((c.Green / 255f) - Mean[1]) / Std[1];
                tensor[2 * Size * Size + idx] = ((c.Blue / 255f) - Mean[2]) / Std[2];
            }
        }

        return tensor;
    }

    public static float[] ToClipTensor(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return ToClipTensor(fs);
    }
}
