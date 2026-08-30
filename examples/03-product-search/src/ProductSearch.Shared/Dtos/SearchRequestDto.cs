using ProductSearch.Shared.Enums;

namespace ProductSearch.Shared.Dtos;

/// <summary>Search request from the UI omnibox or Lens upload.</summary>
public sealed class SearchRequestDto
{
    public string? QueryText { get; set; }
    public string? ImageBase64 { get; set; }
    public QueryMode QueryMode { get; set; } = QueryMode.Text;
    public VectorEngineMode Engine { get; set; } = VectorEngineMode.ZVec;
    public FusionMode Fusion { get; set; } = FusionMode.Rrf;
    public int TopK { get; set; } = 5;
    public bool UseInvertFilter { get; set; } = true;
    public bool UseHybridFts { get; set; } = true;
    public string? Gender { get; set; }
    public string? BaseColour { get; set; }
    public string? Season { get; set; }
    public string? Usage { get; set; }
    public string? MasterCategory { get; set; }
    public Guid? SimilarToProductId { get; set; }
}
