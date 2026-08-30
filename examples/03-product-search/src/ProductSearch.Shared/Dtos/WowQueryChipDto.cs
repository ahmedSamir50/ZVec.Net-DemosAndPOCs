namespace ProductSearch.Shared.Dtos;

/// <summary>Pre-baked demo query chip from wow-queries.json.</summary>
public sealed class WowQueryChipDto
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string QueryText { get; set; } = "";
    public string? Gender { get; set; }
    public string? BaseColour { get; set; }
    public string? Season { get; set; }
    public string? Usage { get; set; }
    public string? MasterCategory { get; set; }
    public string? ImplicitVisualHint { get; set; }
}
