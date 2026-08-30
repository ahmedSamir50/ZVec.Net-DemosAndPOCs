using ZVec.NET;
using ZVec.NET.Mapping;

namespace ProductSearch.Core.Models;

[ZVecCollection("product_image_768")]
public sealed class ProductImageDoc768
{
    [ZVecId]
    public string Id { get; set; } = "";

    [ZVecVector(768, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> ImageEmbedding { get; set; }
}
