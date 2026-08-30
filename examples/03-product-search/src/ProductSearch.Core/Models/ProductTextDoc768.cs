using ZVec.NET;
using ZVec.NET.Mapping;

namespace ProductSearch.Core.Models;

[ZVecCollection("product_text_768")]
public sealed class ProductTextDoc768
{
    [ZVecId]
    public string Id { get; set; } = "";

    public string ConcatenatedText { get; set; } = "";
    public string Gender { get; set; } = "";
    public string BaseColour { get; set; } = "";
    public string Season { get; set; } = "";
    public string Usage { get; set; } = "";

    [ZVecVector(768, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> TextEmbedding { get; set; }
}
