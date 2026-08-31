using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pgvector;

namespace ProductSearch.Core.Data;

/// <summary>Chunked Postgres upsert via INSERT … ON CONFLICT — no EF change-tracker round-trips.</summary>
public static class ProductBulkUpsert
{
    public static async Task UpsertChunkAsync(
        ProductDbContext db,
        IReadOnlyList<ProductEntity> entities,
        int embeddingDim,
        CancellationToken ct = default)
    {
        if (entities.Count == 0)
            return;

        var sql = BuildSql(entities.Count);
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        if (db.Database.CurrentTransaction is { } eftx)
            cmd.Transaction = eftx.GetDbTransaction();

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            Add(cmd, $"@id{i}", e.Id);
            Add(cmd, $"@catalog_id{i}", e.CatalogId);
            Add(cmd, $"@gender{i}", e.Gender);
            Add(cmd, $"@master_category{i}", e.MasterCategory);
            Add(cmd, $"@sub_category{i}", e.SubCategory);
            Add(cmd, $"@article_type{i}", e.ArticleType);
            Add(cmd, $"@base_colour{i}", e.BaseColour);
            Add(cmd, $"@season{i}", e.Season);
            Add(cmd, $"@year{i}", e.Year);
            Add(cmd, $"@usage{i}", e.Usage);
            Add(cmd, $"@product_display_name{i}", e.ProductDisplayName);
            Add(cmd, $"@concatenated_text{i}", e.ConcatenatedText);
            Add(cmd, $"@image_rel_path{i}", e.ImageRelPath);
            AddVector(cmd, $"@text_embedding{i}", e.TextEmbedding);
            AddVector(cmd, $"@image_embedding{i}", e.ImageEmbedding);
            Add(cmd, $"@updated_utc{i}", e.UpdatedUtc.UtcDateTime);
        }

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync(ct).ConfigureAwait(false);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildSql(int rowCount)
    {
        var valueRows = new List<string>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            valueRows.Add(
                $"(@id{i}, @catalog_id{i}, @gender{i}, @master_category{i}, @sub_category{i}, @article_type{i}, " +
                $"@base_colour{i}, @season{i}, @year{i}, @usage{i}, @product_display_name{i}, @concatenated_text{i}, " +
                $"@image_rel_path{i}, @text_embedding{i}, @image_embedding{i}, @updated_utc{i})");
        }

        return $"""
            INSERT INTO products (
                "Id", "CatalogId", "Gender", "MasterCategory", "SubCategory", "ArticleType",
                "BaseColour", "Season", "Year", "Usage", "ProductDisplayName", "ConcatenatedText",
                "ImageRelPath", "TextEmbedding", "ImageEmbedding", "UpdatedUtc")
            VALUES {string.Join(",\n", valueRows)}
            ON CONFLICT ("Id") DO UPDATE SET
                "CatalogId" = EXCLUDED."CatalogId",
                "Gender" = EXCLUDED."Gender",
                "MasterCategory" = EXCLUDED."MasterCategory",
                "SubCategory" = EXCLUDED."SubCategory",
                "ArticleType" = EXCLUDED."ArticleType",
                "BaseColour" = EXCLUDED."BaseColour",
                "Season" = EXCLUDED."Season",
                "Year" = EXCLUDED."Year",
                "Usage" = EXCLUDED."Usage",
                "ProductDisplayName" = EXCLUDED."ProductDisplayName",
                "ConcatenatedText" = EXCLUDED."ConcatenatedText",
                "ImageRelPath" = EXCLUDED."ImageRelPath",
                "TextEmbedding" = EXCLUDED."TextEmbedding",
                "ImageEmbedding" = EXCLUDED."ImageEmbedding",
                "UpdatedUtc" = EXCLUDED."UpdatedUtc"
            """;
    }

    private static void Add(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static void AddVector(System.Data.Common.DbCommand cmd, string name, Vector? vector)
    {
        var p = new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Unknown)
        {
            DataTypeName = "vector",
            Value = (object?)vector ?? DBNull.Value
        };
        cmd.Parameters.Add(p);
    }
}
