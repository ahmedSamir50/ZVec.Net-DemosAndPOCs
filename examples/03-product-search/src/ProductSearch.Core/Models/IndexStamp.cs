namespace ProductSearch.Core.Models;

public sealed record IndexStamp(
    string ModelId,
    int EmbeddingDim,
    string EncodePipelineVersion,
    int IngestOffset);
