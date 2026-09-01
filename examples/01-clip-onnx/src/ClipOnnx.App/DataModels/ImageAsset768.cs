using ZVec.NET;
using ZVec.NET.Mapping;

namespace ClipOnnx.App.DataModels;

/// <summary>768-d gallery entity (ViT-L/14).</summary>
[ZVecCollection("clip_gallery_768")]
public sealed class ImageAsset768
{
    [ZVecId]
    public string Id { get; set; } = "";

    public string Path { get; set; } = "";

    [ZVecVector(768, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
