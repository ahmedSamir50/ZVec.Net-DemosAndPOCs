using ZVec.NET;
using ZVec.NET.Mapping;

namespace ProductSearch.Core.Models;

[ZVecCollection("product_text_1152")]
public sealed class ProductTextDoc1152
{
    [ZVecId]
    public string Id { get; set; } = "";

    public string ConcatenatedText { get; set; } = "";
    public string Gender { get; set; } = "";
    public string BaseColour { get; set; } = "";
    public string Season { get; set; } = "";
    public string Usage { get; set; } = "";
    public string MasterCategory { get; set; } = "";

    [ZVecVector(1152, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> TextEmbedding { get; set; }
}
