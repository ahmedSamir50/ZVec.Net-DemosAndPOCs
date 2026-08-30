using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductSearch.Core.Data.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:vector", ",,");

        migrationBuilder.CreateTable(
            name: "products",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CatalogId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Gender = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MasterCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SubCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ArticleType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                BaseColour = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Season = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Year = table.Column<int>(type: "integer", nullable: false),
                Usage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ProductDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ConcatenatedText = table.Column<string>(type: "text", nullable: false),
                ImageRelPath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                TextEmbedding = table.Column<object>(type: "vector(768)", nullable: true),
                ImageEmbedding = table.Column<object>(type: "vector(768)", nullable: true),
                UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_products", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_products_CatalogId",
            table: "products",
            column: "CatalogId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "products");
    }
}
