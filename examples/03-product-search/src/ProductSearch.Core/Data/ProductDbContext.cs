using Microsoft.EntityFrameworkCore;

namespace ProductSearch.Core.Data;

public sealed class ProductEntity
{
    public Guid Id { get; set; }
    public string CatalogId { get; set; } = "";
    public string Gender { get; set; } = "";
    public string MasterCategory { get; set; } = "";
    public string SubCategory { get; set; } = "";
    public string ArticleType { get; set; } = "";
    public string BaseColour { get; set; } = "";
    public string Season { get; set; } = "";
    public int Year { get; set; }
    public string Usage { get; set; } = "";
    public string ProductDisplayName { get; set; } = "";
    public string ConcatenatedText { get; set; } = "";
    public string ImageRelPath { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class ProductDbContext : DbContext
{
    public ProductDbContext(DbContextOptions<ProductDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<ProductEmbedding768Entity> Embeddings768 => Set<ProductEmbedding768Entity>();
    public DbSet<ProductEmbedding1152Entity> Embeddings1152 => Set<ProductEmbedding1152Entity>();

    public int ClearEmbeddings(int embeddingDim)
        => embeddingDim switch
        {
            768 => Embeddings768.ExecuteDelete(),
            1152 => Embeddings1152.ExecuteDelete(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(embeddingDim), embeddingDim, "Unsupported embedding dimension.")
        };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<ProductEntity>(entity =>
        {
            entity.ToTable("products");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CatalogId).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => e.CatalogId).IsUnique();
            entity.Property(e => e.Gender).HasMaxLength(32);
            entity.Property(e => e.MasterCategory).HasMaxLength(64);
            entity.Property(e => e.SubCategory).HasMaxLength(64);
            entity.Property(e => e.ArticleType).HasMaxLength(64);
            entity.Property(e => e.BaseColour).HasMaxLength(64);
            entity.Property(e => e.Season).HasMaxLength(32);
            entity.Property(e => e.Usage).HasMaxLength(64);
            entity.Property(e => e.ProductDisplayName).HasMaxLength(256);
            entity.Property(e => e.ImageRelPath).HasMaxLength(256);
            entity.Property(e => e.UpdatedUtc).HasDefaultValueSql("now()");
        });

        ConfigureEmbeddingTable<ProductEmbedding768Entity>(modelBuilder, "product_embeddings_768", 768);
        ConfigureEmbeddingTable<ProductEmbedding1152Entity>(modelBuilder, "product_embeddings_1152", 1152);
    }

    private static void ConfigureEmbeddingTable<TEntity>(ModelBuilder modelBuilder, string tableName, int dim)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(nameof(ProductEmbedding768Entity.Id));
            entity.Property(nameof(ProductEmbedding768Entity.TextEmbedding)).HasColumnType($"vector({dim})");
            entity.Property(nameof(ProductEmbedding768Entity.ImageEmbedding)).HasColumnType($"vector({dim})");
            entity.Property(nameof(ProductEmbedding768Entity.UpdatedUtc)).HasDefaultValueSql("now()");
            entity.HasOne<ProductEntity>()
                .WithMany()
                .HasForeignKey(nameof(ProductEmbedding768Entity.Id))
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
