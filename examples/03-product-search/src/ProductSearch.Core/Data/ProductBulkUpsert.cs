using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using ProductSearch.Core.Configuration;

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

        var sql = BuildSql(entities.Count, embeddingDim);
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            Add(cmd, $"id{i}", e.Id);
            Add(cmd, $"catalog_id{i}", e.CatalogId);
            Add(cmd, $"gender{i}", e.Gender);
            Add(cmd, $"master_category{i}", e.MasterCategory);
            Add(cmd, $"sub_category{i}", e.SubCategory);
            Add(cmd, $"article_type{i}", e.ArticleType);
            Add(cmd, $"base_colour{i}", e.BaseColour);
            Add(cmd, $"season{i}", e.Season);
            Add(cmd, $"year{i}", e.Year);
            Add(cmd, $"usage{i}", e.Usage);
            Add(cmd, $"product_display_name{i}", e.ProductDisplayName);
            Add(cmd, $"concatenated_text{i}", e.ConcatenatedText);
            Add(cmd, $"image_rel_path{i}", e.ImageRelPath);
            AddVector(cmd, $"text_embedding{i}", e.TextEmbedding);
            AddVector(cmd, $"image_embedding{i}", e.ImageEmbedding);
            Add(cmd, $"updated_utc{i}", e.UpdatedUtc.UtcDateTime);
        }

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync(ct).ConfigureAwait(false);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildSql(int rowCount, int embeddingDim)
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
                id, catalog_id, gender, master_category, sub_category, article_type,
                base_colour, season, year, usage, product_display_name, concatenated_text,
                image_rel_path, text_embedding, image_embedding, updated_utc)
            VALUES {string.Join(",\n", valueRows)}
            ON CONFLICT (id) DO UPDATE SET
                catalog_id = EXCLUDED.catalog_id,
                gender = EXCLUDED.gender,
                master_category = EXCLUDED.master_category,
                sub_category = EXCLUDED.sub_category,
                article_type = EXCLUDED.article_type,
                base_colour = EXCLUDED.base_colour,
                season = EXCLUDED.season,
                year = EXCLUDED.year,
                usage = EXCLUDED.usage,
                product_display_name = EXCLUDED.product_display_name,
                concatenated_text = EXCLUDED.concatenated_text,
                image_rel_path = EXCLUDED.image_rel_path,
                text_embedding = EXCLUDED.text_embedding,
                image_embedding = EXCLUDED.image_embedding,
                updated_utc = EXCLUDED.updated_utc
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
