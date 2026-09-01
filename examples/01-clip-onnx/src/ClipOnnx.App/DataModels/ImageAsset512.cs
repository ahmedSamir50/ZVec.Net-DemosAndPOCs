using ZVec.NET;
using ZVec.NET.Mapping;

namespace ClipOnnx.App.DataModels;

/// <summary>512-d gallery entity for typed collections (ViT-B/32 and ViT-B/16).</summary>
[ZVecCollection("clip_gallery_512")]
public sealed class ImageAsset512
{
    [ZVecId]
    public string Id { get; set; } = "";

    public string Path { get; set; } = "";

    [ZVecVector(512, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
