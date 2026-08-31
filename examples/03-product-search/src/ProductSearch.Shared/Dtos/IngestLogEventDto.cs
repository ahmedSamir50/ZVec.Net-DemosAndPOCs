namespace ProductSearch.Shared.Dtos;

/// <summary>Single line in the ingest live event log.</summary>
public sealed class IngestLogEventDto
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "Info";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public long? ElapsedMs { get; set; }
}
