namespace ProductSearch.Core.Models;

public sealed class CatalogProduct
{
    public string CatalogId { get; set; } = "";
    public string Gender { get; set; } = "";
    public string MasterCategory { get; set; } = "";
    public string SubCategory { get; set; } = "";
    public string ArticleType { get; set; } = "";
    public string BaseColour { get; set; } = "";
    public string Season { get; set; } = "";
    public int Year { get; set; }
    public string Usage { get; set; } = "";
    public string ProductDisplayName { get; set; } = "";
    public string ConcatenatedText { get; set; } = "";
    public string ImageRelPath { get; set; } = "";
}
