namespace ProductSearch.Shared.Dtos;

/// <summary>Postgres connection details for the Status page.</summary>
public sealed class PostgresConnectionDto
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string VolumeName { get; set; } = "productsearch-pgdata";
}
