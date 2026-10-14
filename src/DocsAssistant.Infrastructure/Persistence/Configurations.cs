using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Handoff;
using DocsAssistant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

		// Optimistic concurrency on Postgres' xmin system column: no extra column, and two workers
		// claiming the same Pending document cannot both win.
		builder.Property<uint>(PostgresNames.RowVersionProperty).IsRowVersion();
	}
}

internal sealed class DocumentContentConfiguration : IEntityTypeConfiguration<DocumentContent>
{
	public void Configure(EntityTypeBuilder<DocumentContent> builder)
	{
		builder.HasKey(c => c.DocumentId);
		builder.HasOne<Document>().WithOne().HasForeignKey<DocumentContent>(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(c => c.TenantId);
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

		// A comparer by content: without it EF compares array references and cannot tell whether an
		// embedding changed.
		builder.Property(c => c.Embedding)
			.HasColumnName(PostgresNames.EmbeddingColumn)
			.HasConversion(new EmbeddingConverter(), new ValueComparer<float[]>(
				(left, right) => left!.SequenceEqual(right!),
				vector => vector.Length == 0 ? 0 : HashCode.Combine(vector.Length, vector[0], vector[vector.Length - 1]),
				vector => vector.ToArray()))
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

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
	public void Configure(EntityTypeBuilder<Conversation> builder)
	{
		builder.HasKey(c => c.Id);
		builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(c => new { c.TenantId, c.LastMessageAt });
	}
}

internal sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
	private const int OutcomeMaxLength = 40;

	public void Configure(EntityTypeBuilder<ConversationMessage> builder)
	{
		builder.ToTable("messages");
		builder.HasKey(m => m.Id);
		builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(m => m.TenantId);

		// The transcript reads the latest messages of one conversation.
		builder.HasIndex(m => new { m.ConversationId, m.CreatedAt });

		builder.Property(m => m.Role).HasConversion<string>();
		builder.Property(m => m.Text).HasMaxLength(ConversationMessage.MaxTextLength);
		builder.Property(m => m.Outcome).HasMaxLength(OutcomeMaxLength);
		builder.Property(m => m.Citations)
			.HasConversion(new CitationsConverter(), new CitationsComparer())
			.HasColumnType("jsonb");
	}
}

internal sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
	public void Configure(EntityTypeBuilder<Lead> builder)
	{
		builder.HasKey(l => l.Id);
		builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);

		// A conversation can be deleted (data retention) without losing the lead that came from it.
		builder.HasOne<Conversation>().WithMany().HasForeignKey(l => l.ConversationId).OnDelete(DeleteBehavior.SetNull);

		builder.Property(l => l.VisitorEmail).HasMaxLength(Lead.EmailMaxLength);
		builder.Property(l => l.VisitorName).HasMaxLength(Lead.NameMaxLength);
		builder.Property(l => l.Message).HasMaxLength(Lead.MessageMaxLength);
		builder.Property(l => l.LastError).HasMaxLength(Lead.ErrorMaxLength);
		builder.Property(l => l.Language).HasConversion<string>();
		builder.Property(l => l.Status).HasConversion<string>();

		builder.HasIndex(l => new { l.TenantId, l.CreatedAt });

		// The worker polls for due leads; a partial index keeps that scan tiny.
		builder.HasIndex(l => l.NextAttemptAt).HasFilter("status = 'Pending'");

		// Same as documents: two workers claiming the same lead cannot both win.
		builder.Property<uint>(PostgresNames.RowVersionProperty).IsRowVersion();
	}
}
