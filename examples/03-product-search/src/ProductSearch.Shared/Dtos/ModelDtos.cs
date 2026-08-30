namespace ProductSearch.Shared.Dtos;

/// <summary>SigLIP model definition exposed to the UI.</summary>
public sealed class ModelDefinitionDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int EmbeddingDim { get; set; }
    public int ImageSize { get; set; }
}

/// <summary>Model list response.</summary>
public sealed class ModelsResponseDto
{
    public IReadOnlyList<ModelDefinitionDto> Models { get; set; } = [];
    public string ActiveModelId { get; set; } = "";
}

/// <summary>Select model request.</summary>
public sealed class ModelSelectRequestDto
{
    public string ModelId { get; set; } = "";
}

/// <summary>Select model result.</summary>
public sealed class ModelSelectResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? ActiveModelId { get; set; }
}
