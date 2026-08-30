using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using ProductSearch.Core.Configuration;
using ProductSearch.Core.Models;

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
    public Vector? TextEmbedding { get; set; }
    public Vector? ImageEmbedding { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class ProductDbContext : DbContext
{
    private readonly int _embeddingDim;

    public ProductDbContext(DbContextOptions<ProductDbContext> options, IOptions<ProductSearchOptions> searchOptions)
        : base(options)
    {
        _embeddingDim = SigLipModelCatalog.Get(searchOptions.Value.ActiveModelId).EmbeddingDim;
    }

    public DbSet<ProductEntity> Products => Set<ProductEntity>();

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
            entity.Property(e => e.TextEmbedding).HasColumnType($"vector({_embeddingDim})");
            entity.Property(e => e.ImageEmbedding).HasColumnType($"vector({_embeddingDim})");
            entity.Property(e => e.UpdatedUtc).HasDefaultValueSql("now()");
        });
    }
}
