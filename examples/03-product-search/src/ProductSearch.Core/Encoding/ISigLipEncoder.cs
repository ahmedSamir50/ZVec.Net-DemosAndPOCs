using ProductSearch.Core.Models;

namespace ProductSearch.Core.Encoding;

public interface ISigLipEncoder
{
    bool IsReady { get; }
    string? NotReadyReason { get; }
    string? ActiveModelId { get; }
    int EmbeddingDim { get; }
    int ImageSize { get; }
    int IntraOpNumThreads { get; }
    void InitializeFromDisk(string modelsDir, SigLipModelDefinition model);
    float[] EncodeImage(Stream imageStream);
    float[] EncodeImage(string filePath);
    float[] EncodeText(string text);
}
