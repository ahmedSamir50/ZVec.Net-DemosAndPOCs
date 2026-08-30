namespace ProductSearch.Shared.Dtos;

/// <summary>Single ranked search hit.</summary>
public sealed class SearchHitDto
{
    public ProductCardDto Product { get; set; } = new();
    public double Score { get; set; }
    public double SimilarityPercent { get; set; }
    public int Rank { get; set; }
    public bool FromText { get; set; }
    public bool FromImage { get; set; }
    public bool FromFts { get; set; }
    public string Engine { get; set; } = "";
}
