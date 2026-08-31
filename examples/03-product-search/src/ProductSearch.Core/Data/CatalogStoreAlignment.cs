namespace ProductSearch.Core.Data;

/// <summary>Compares Postgres embedding-table counts to ZVec doc counts.</summary>
public static class CatalogStoreAlignment
{
    public static bool CountsMatch(int embeddingCount, long textCount, long imageCount)
        => embeddingCount > 0
           && embeddingCount == textCount
           && embeddingCount == imageCount;

    public static bool HasSplitBrain(int embeddingCount, long textCount, long imageCount)
    {
        if (embeddingCount == 0 && textCount == 0 && imageCount == 0)
            return false;

        return embeddingCount != textCount || embeddingCount != imageCount;
    }

    public static string SplitBrainMessage(int embeddingCount, long textCount, long imageCount)
        => $"Count mismatch — PG embeddings={embeddingCount}, ZVec text={textCount}, ZVec image={imageCount}. " +
           "Reset indexes, then re-ingest.";
}
