using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pgvector.EntityFrameworkCore;
using ProductSearch.Core.Data;

#nullable disable

namespace ProductSearch.Core.Data.Migrations;

[DbContext(typeof(ProductDbContext))]
public partial class ProductDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.10")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.HasPostgresExtension(modelBuilder, "vector");
        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("ProductSearch.Core.Data.ProductEntity", b =>
            {
                b.Property<Guid>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid");

                b.Property<string>("ArticleType")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)");

                b.Property<string>("BaseColour")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)");

                b.Property<string>("CatalogId")
                    .IsRequired()
                    .HasMaxLength(32)
                    .HasColumnType("character varying(32)");

                b.Property<string>("ConcatenatedText")
                    .IsRequired()
                    .HasColumnType("text");

                b.Property<string>("Gender")
                    .IsRequired()
                    .HasMaxLength(32)
                    .HasColumnType("character varying(32)");

                b.Property<object>("ImageEmbedding")
                    .HasColumnType("vector(768)");

                b.Property<string>("ImageRelPath")
                    .IsRequired()
                    .HasMaxLength(256)
                    .HasColumnType("character varying(256)");

                b.Property<string>("MasterCategory")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)");

                b.Property<string>("ProductDisplayName")
                    .IsRequired()
                    .HasMaxLength(256)
                    .HasColumnType("character varying(256)");

                b.Property<string>("Season")
                    .IsRequired()
                    .HasMaxLength(32)
                    .HasColumnType("character varying(32)");

                b.Property<string>("SubCategory")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)");

                b.Property<object>("TextEmbedding")
                    .HasColumnType("vector(768)");

                b.Property<DateTimeOffset>("UpdatedUtc")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("timestamp with time zone")
                    .HasDefaultValueSql("now()");

                b.Property<string>("Usage")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)");

                b.Property<int>("Year")
                    .HasColumnType("integer");

                b.HasKey("Id");

                b.HasIndex("CatalogId")
                    .IsUnique();

                b.ToTable("products");
            });
#pragma warning restore 612, 618
    }
}
