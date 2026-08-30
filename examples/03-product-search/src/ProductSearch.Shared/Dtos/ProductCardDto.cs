namespace ProductSearch.Shared.Dtos;

/// <summary>Product card shown in search results.</summary>
public sealed class ProductCardDto
{
    public Guid Id { get; set; }
    public int CatalogId { get; set; }
    public string ProductDisplayName { get; set; } = "";
    public string Gender { get; set; } = "";
    public string MasterCategory { get; set; } = "";
    public string SubCategory { get; set; } = "";
    public string ArticleType { get; set; } = "";
    public string BaseColour { get; set; } = "";
    public string Season { get; set; } = "";
    public int Year { get; set; }
    public string Usage { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string ConcatenatedText { get; set; } = "";
}
