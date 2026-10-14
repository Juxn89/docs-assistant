using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocsAssistant.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PersistenceTests(PostgresFixture postgres)
{
	private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task CannotReadOtherTenantData()
	{
		var (alice, aliceDoc) = await SeedTenantWithChunkAsync("Alice", "Política de devoluciones: 30 días.");
		var (bob, _) = await SeedTenantWithChunkAsync("Bob", "Horario: lunes a viernes.");

		var asBob = new TenantContext();
		asBob.Set(bob.Id);
		await using var db = postgres.CreateContext(asBob);

		Assert.Null(await db.Documents.SingleOrDefaultAsync(d => d.Id == aliceDoc.Id));
		Assert.DoesNotContain(await db.Chunks.ToListAsync(), c => c.TenantId == alice.Id);
		Assert.All(await db.Documents.ToListAsync(), d => Assert.Equal(bob.Id, d.TenantId));
	}

	[Fact]
	public async Task WithNoTenantInContext_QueriesFailClosed()
	{
		await SeedTenantWithChunkAsync("Carol", "Envíos gratis desde 50 USD.");

		await using var db = postgres.CreateContext(new TenantContext());

		Assert.Empty(await db.Documents.ToListAsync());
		Assert.Empty(await db.Chunks.ToListAsync());
	}

	[Fact]
	public async Task SameContentTwiceForOneTenant_IsRejectedByTheDatabase()
	{
		var (tenant, document) = await SeedTenantWithChunkAsync("Dave", "Garantía de un año.");
		var context = new TenantContext();
		context.Set(tenant.Id);
		await using var db = postgres.CreateContext(context);

		db.Documents.Add(Document.Register(
			tenant.Id, "Copy", DocumentSourceType.Pdf, "copy.pdf", document.ContentHash, ContentLanguage.Spanish, Now));

		var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
	}

	[Fact]
	public async Task SearchVector_IsGeneratedWithTheDocumentLanguage()
	{
		var (tenant, _) = await SeedTenantWithChunkAsync("Erin", "Las devoluciones se procesan en tres días hábiles.");

		await using var connection = new NpgsqlConnection(postgres.ConnectionString);
		await connection.OpenAsync();
		await using var command = new NpgsqlCommand(
			"SELECT count(*) FROM chunks WHERE tenant_id = @tenant "
			+ "AND search_vector @@ websearch_to_tsquery('spanish', 'devolución procesada')",
			connection);
		command.Parameters.AddWithValue("tenant", tenant.Id.Value);

		// Spanish stemming: "devolución procesada" matches "devoluciones se procesan".
		Assert.Equal(1L, await command.ExecuteScalarAsync());
	}

	[Fact]
	public async Task Embedding_RoundTripsThroughPgvector()
	{
		var (tenant, document) = await SeedTenantWithChunkAsync("Frank", "Texto.");
		var context = new TenantContext();
		context.Set(tenant.Id);
		await using var db = postgres.CreateContext(context);

		var chunk = await db.Chunks.SingleAsync(c => c.DocumentId == document.Id);

		Assert.Equal(PostgresNames.EmbeddingDimensions, chunk.Embedding.Length);
		Assert.Equal(UnitVector()[0], chunk.Embedding[0]);
	}

	private async Task<(Tenant Tenant, Document Document)> SeedTenantWithChunkAsync(string name, string text)
	{
		var tenant = Tenant.Create(
			name, $"pk_{Guid.NewGuid():N}", $"{name}@example.com", ContentLanguage.Spanish, [], Now);
		var context = new TenantContext();
		context.Set(tenant.Id);
		await using var db = postgres.CreateContext(context);

		var document = Document.Register(
			tenant.Id,
			$"{name} FAQ",
			DocumentSourceType.Markdown,
			"faq.md",
			ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(name + text)),
			ContentLanguage.Spanish,
			Now);
		document.StartProcessing(Now);
		document.MarkReady(1, Now);

		db.Tenants.Add(tenant);
		db.Documents.Add(document);
		db.Chunks.Add(Chunk.Create(document, new ChunkDraft(0, null, text, TokenEstimator.Estimate(text)), UnitVector()));
		await db.SaveChangesAsync();

		return (tenant, document);
	}

	private static float[] UnitVector()
	{
		var vector = new float[PostgresNames.EmbeddingDimensions];
		vector[0] = 1f;
		return vector;
	}
}
