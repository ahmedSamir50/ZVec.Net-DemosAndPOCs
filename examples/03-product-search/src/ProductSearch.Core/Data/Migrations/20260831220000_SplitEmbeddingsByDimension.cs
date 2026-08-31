using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductSearch.Core.Data.Migrations;

public partial class SplitEmbeddingsByDimension : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "product_embeddings_768",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TextEmbedding = table.Column<object>(type: "vector(768)", nullable: true),
                ImageEmbedding = table.Column<object>(type: "vector(768)", nullable: true),
                UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_embeddings_768", x => x.Id);
                table.ForeignKey(
                    name: "FK_product_embeddings_768_products_Id",
                    column: x => x.Id,
                    principalTable: "products",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "product_embeddings_1152",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TextEmbedding = table.Column<object>(type: "vector(1152)", nullable: true),
                ImageEmbedding = table.Column<object>(type: "vector(1152)", nullable: true),
                UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_embeddings_1152", x => x.Id);
                table.ForeignKey(
                    name: "FK_product_embeddings_1152_products_Id",
                    column: x => x.Id,
                    principalTable: "products",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.DropColumn(
            name: "ImageEmbedding",
            table: "products");

        migrationBuilder.DropColumn(
            name: "TextEmbedding",
            table: "products");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<object>(
            name: "ImageEmbedding",
            table: "products",
            type: "vector(768)",
            nullable: true);

        migrationBuilder.AddColumn<object>(
            name: "TextEmbedding",
            table: "products",
            type: "vector(768)",
            nullable: true);

        migrationBuilder.DropTable(
            name: "product_embeddings_1152");

        migrationBuilder.DropTable(
            name: "product_embeddings_768");
    }
}
