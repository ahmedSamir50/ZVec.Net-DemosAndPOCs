namespace ProductSearch.Shared.Constants;

/// <summary>API route paths.</summary>
public static class ApiRoutes
{
    public const string Search = "/api/search";
    public const string SearchSimilar = "/api/search/similar";
    public const string WowQueries = "/api/search/wow-queries";
    public const string Ingest = "/api/ingest";
    public const string IngestOptimize = "/api/ingest/optimize";
    public const string IngestResetIndexes = "/api/ingest/reset-indexes";
    public const string IngestResetCatalog = "/api/ingest/reset-catalog";
    public const string Status = "/api/status";
    public const string Models = "/api/models";
    public const string ModelsSelect = "/api/models/select";
    public const string Media = "/api/media";
}
