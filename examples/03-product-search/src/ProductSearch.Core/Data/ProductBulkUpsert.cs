using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pgvector;

namespace ProductSearch.Core.Data;

/// <summary>Chunked Postgres upsert via INSERT … ON CONFLICT — catalog in products, vectors in the active dim table.</summary>
public static class ProductBulkUpsert
{
    public static async Task UpsertChunkAsync(
        ProductDbContext db,
        IReadOnlyList<ProductEntity> catalog,
        IReadOnlyList<ProductEmbeddingWrite> embeddings,
        int embeddingDim,
        CancellationToken ct = default)
    {
        if (catalog.Count == 0)
            return;

        if (catalog.Count != embeddings.Count)
        {
            throw new ArgumentException(
                $"Catalog count {catalog.Count} does not match embedding count {embeddings.Count}.",
                nameof(embeddings));
        }

        var table = EmbeddingTable(embeddingDim);
        for (var i = 0; i < embeddings.Count; i++)
        {
            var row = embeddings[i];
            var textLen = row.TextEmbedding.ToArray().Length;
            var imageLen = row.ImageEmbedding.ToArray().Length;
            if (textLen != embeddingDim || imageLen != embeddingDim)
            {
                throw new InvalidOperationException(
                    $"Embedding dim mismatch for {row.Id}: expected {embeddingDim}, got text={textLen} image={imageLen}.");
            }
        }

        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        await ExecuteAsync(db, BuildCatalogSql(catalog.Count), cmd =>
        {
            for (var i = 0; i < catalog.Count; i++)
            {
                var e = catalog[i];
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
                Add(cmd, $"@updated_utc{i}", e.UpdatedUtc.UtcDateTime);
            }
        }, ct).ConfigureAwait(false);

        await ExecuteAsync(db, BuildEmbeddingSql(table, embeddings.Count), cmd =>
        {
            for (var i = 0; i < embeddings.Count; i++)
            {
                var e = embeddings[i];
                Add(cmd, $"@id{i}", e.Id);
                AddVector(cmd, $"@text_embedding{i}", e.TextEmbedding);
                AddVector(cmd, $"@image_embedding{i}", e.ImageEmbedding);
                Add(cmd, $"@updated_utc{i}", e.UpdatedUtc.UtcDateTime);
            }
        }, ct).ConfigureAwait(false);
    }

    private static string EmbeddingTable(int embeddingDim)
        => embeddingDim switch
        {
            768 => "product_embeddings_768",
            1152 => "product_embeddings_1152",
            _ => throw new ArgumentOutOfRangeException(
                nameof(embeddingDim), embeddingDim, "Unsupported embedding dimension.")
        };

    private static async Task ExecuteAsync(
        ProductDbContext db,
        string sql,
        Action<System.Data.Common.DbCommand> bind,
        CancellationToken ct)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        if (db.Database.CurrentTransaction is { } eftx)
            cmd.Transaction = eftx.GetDbTransaction();
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildCatalogSql(int rowCount)
    {
        var valueRows = new List<string>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            valueRows.Add(
                $"(@id{i}, @catalog_id{i}, @gender{i}, @master_category{i}, @sub_category{i}, @article_type{i}, " +
                $"@base_colour{i}, @season{i}, @year{i}, @usage{i}, @product_display_name{i}, @concatenated_text{i}, " +
                $"@image_rel_path{i}, @updated_utc{i})");
        }

        return $"""
            INSERT INTO products (
                "Id", "CatalogId", "Gender", "MasterCategory", "SubCategory", "ArticleType",
                "BaseColour", "Season", "Year", "Usage", "ProductDisplayName", "ConcatenatedText",
                "ImageRelPath", "UpdatedUtc")
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
                "UpdatedUtc" = EXCLUDED."UpdatedUtc"
            """;
    }

    private static string BuildEmbeddingSql(string table, int rowCount)
    {
        var valueRows = new List<string>(rowCount);
        for (var i = 0; i < rowCount; i++)
            valueRows.Add($"(@id{i}, @text_embedding{i}, @image_embedding{i}, @updated_utc{i})");

        return $"""
            INSERT INTO "{table}" (
                "Id", "TextEmbedding", "ImageEmbedding", "UpdatedUtc")
            VALUES {string.Join(",\n", valueRows)}
            ON CONFLICT ("Id") DO UPDATE SET
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
