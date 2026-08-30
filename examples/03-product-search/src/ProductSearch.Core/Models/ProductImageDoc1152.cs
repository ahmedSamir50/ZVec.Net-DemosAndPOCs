using ZVec.NET;
using ZVec.NET.Mapping;

namespace ProductSearch.Core.Models;

[ZVecCollection("product_image_1152")]
public sealed class ProductImageDoc1152
{
    [ZVecId]
    public string Id { get; set; } = "";

    [ZVecVector(1152, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> ImageEmbedding { get; set; }
}
