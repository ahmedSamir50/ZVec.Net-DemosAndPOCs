using Pgvector;

namespace ProductSearch.Core.Data;

public sealed class ProductEmbedding768Entity
{
    public Guid Id { get; set; }
    public Vector? TextEmbedding { get; set; }
    public Vector? ImageEmbedding { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class ProductEmbedding1152Entity
{
    public Guid Id { get; set; }
    public Vector? TextEmbedding { get; set; }
    public Vector? ImageEmbedding { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public readonly record struct ProductEmbeddingWrite(
    Guid Id,
    Vector TextEmbedding,
    Vector ImageEmbedding,
    DateTimeOffset UpdatedUtc);
