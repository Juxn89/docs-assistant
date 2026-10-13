using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace DocsAssistant.Infrastructure.Persistence;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
	private const int NameMaxLength = 200;
	private const int PublicKeyMaxLength = 64;
	private const int EmailMaxLength = 320;

	public void Configure(EntityTypeBuilder<Tenant> builder)
	{
		builder.HasKey(t => t.Id);
		builder.Property(t => t.Name).HasMaxLength(NameMaxLength);
		builder.Property(t => t.PublicKey).HasMaxLength(PublicKeyMaxLength);
		builder.HasIndex(t => t.PublicKey).IsUnique();
		builder.Property(t => t.HandoffEmail).HasMaxLength(EmailMaxLength);
		builder.Property(t => t.DefaultLanguage).HasConversion<string>();

		// The domain exposes a read-only view; EF writes the backing list (Postgres text[]).
		builder.Ignore(t => t.AllowedOrigins);
		builder.Property<List<string>>("_allowedOrigins").HasColumnName("allowed_origins");
	}
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
	private const int TitleMaxLength = 300;
	private const int SourceUriMaxLength = 2048;
	private const int ErrorMaxLength = 2000;

	public void Configure(EntityTypeBuilder<Document> builder)
	{
		builder.HasKey(d => d.Id);
		builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);

		builder.Property(d => d.Title).HasMaxLength(TitleMaxLength);
		builder.Property(d => d.SourceUri).HasMaxLength(SourceUriMaxLength);
		builder.Property(d => d.SourceType).HasConversion<string>();
		builder.Property(d => d.Status).HasConversion<string>();
		builder.Property(d => d.Language).HasConversion<string>();
		builder.Property(d => d.Error).HasMaxLength(ErrorMaxLength);
		builder.Property(d => d.ContentHash).HasMaxLength(ContentHash.HexLength).IsFixedLength();

		// Ingestion idempotency: the same bytes uploaded twice to one tenant are one document.
		builder.HasIndex(d => new { d.TenantId, d.ContentHash }).IsUnique();

		// The worker polls for pending work; a partial index keeps that scan tiny.
		builder.HasIndex(d => d.CreatedAt).HasFilter("status = 'Pending'");
	}
}

internal sealed class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
	public void Configure(EntityTypeBuilder<Chunk> builder)
	{
		builder.ToTable(PostgresNames.ChunksTable);
		builder.HasKey(c => c.Id);
		builder.HasOne<Document>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(c => new { c.DocumentId, c.Ordinal }).IsUnique();
		builder.HasIndex(c => c.TenantId);

		builder.Property(c => c.Language).HasConversion<string>();

		builder.Property(c => c.Embedding)
			.HasColumnName(PostgresNames.EmbeddingColumn)
			.HasConversion<EmbeddingConverter>()
			.HasColumnType($"vector({PostgresNames.EmbeddingDimensions})");

		// HNSW over cosine distance: e5 embeddings are compared by cosine similarity.
		builder.HasIndex(c => c.Embedding)
			.HasMethod("hnsw")
			.HasOperators("vector_cosine_ops");

		// Full-text side of the hybrid search, computed by Postgres so it can never drift from the text.
		builder.Property<NpgsqlTsVector>("SearchVector")
			.HasColumnName(PostgresNames.SearchVectorColumn)
			.HasComputedColumnSql(PostgresNames.SearchVectorSql, stored: true);
		builder.HasIndex("SearchVector").HasMethod("gin");
	}
}
