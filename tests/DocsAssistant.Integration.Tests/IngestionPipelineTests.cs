using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Application.Features.Tenants;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace DocsAssistant.Integration.Tests;

[CollectionDefinition(Name)]
public sealed class IngestionCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<EmbedderFixture>
{
	public const string Name = "ingestion";
}

/// <summary>
/// The whole pipeline wired exactly as the API wires it (AddInfrastructure), against real Postgres and
/// the real embedding model. The hosted worker is not started: tests drive ProcessDocument directly so
/// they are deterministic.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class IngestionPipelineTests : IDisposable
{
	private readonly PostgresFixture _postgres;
	private readonly ServiceProvider _services;

	public IngestionPipelineTests(PostgresFixture postgres)
	{
		_postgres = postgres;
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}"] = postgres.ConnectionString,
				["Ingestion:MaxAttempts"] = "2",
			})
			.Build();
		_services = new ServiceCollection()
			.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
			.AddInfrastructure(configuration)
			.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
	}

	public void Dispose() => _services.Dispose();

	[Fact]
	public async Task Pdf_IsIngestedAndRetrievableBothWays()
	{
		var tenantId = await CreateTenantAsync();
		// PdfPig's standard Helvetica has no accented glyphs, so the PDF text is unaccented; the queries
		// below keep their accents, which is also what real users type.
		var pdf = ExtractorTests.BuildPdf(
			"Las devoluciones se aceptan dentro de los 30 dias posteriores a la entrega, con el recibo de compra.",
			"Los envios son gratuitos para pedidos superiores a 50 dolares en todo el pais.");

		var job = await UploadAsync(tenantId, "politicas.pdf", pdf);
		var outcome = await ProcessAsync(job);

		Assert.Equal(ProcessingOutcome.Ready, outcome);
		var document = await WithTenantAsync(tenantId, (db, _) => db.Documents.SingleAsync(d => d.Id == job.DocumentId));
		Assert.Equal(DocumentStatus.Ready, document.Status);
		Assert.Equal(ContentLanguage.Spanish, document.Language);
		Assert.Equal(2, document.ChunkCount);

		// Full text, with Spanish stemming: "devolución" finds "devoluciones".
		var fullText = await TenantIdsMatchingAsync(
			tenantId,
			"SELECT heading FROM chunks WHERE tenant_id = @tenant AND search_vector @@ websearch_to_tsquery('spanish', 'devolución')");
		Assert.Equal(["p. 1"], fullText);

		// Vector: the shipping question lands on page 2.
		var embedder = _services.GetRequiredService<IEmbedder>();
		var query = await embedder.EmbedQueryAsync("¿Cuánto cuesta el envío?", CancellationToken.None);
		var nearest = await NearestHeadingAsync(tenantId, query);
		Assert.Equal("p. 2", nearest);
	}

	[Fact]
	public async Task SameFileTwice_IsOneDocument()
	{
		var tenantId = await CreateTenantAsync();
		var bytes = "Horario de atención: lunes a viernes de 8 a 17 horas."u8.ToArray();

		await UploadAsync(tenantId, "horario.txt", bytes);
		await using var scope = _services.CreateAsyncScope();
		var second = await scope.ServiceProvider.GetRequiredService<UploadDocument>()
			.HandleAsync(new UploadDocumentRequest(tenantId.Value, "copia.txt", "text/plain", bytes, null), CancellationToken.None);

		Assert.True(second.Value.Duplicate);
		var count = await WithTenantAsync(tenantId, (db, _) => db.Documents.CountAsync());
		Assert.Equal(1, count);
	}

	[Fact]
	public async Task TwoWorkersClaimingTheSameDocument_OnlyOneWins()
	{
		var tenantId = await CreateTenantAsync();
		var job = await UploadAsync(tenantId, "doc.txt", "Texto de prueba para la carrera entre workers."u8.ToArray());

		await using var first = _services.CreateAsyncScope();
		await using var second = _services.CreateAsyncScope();
		var a = await LoadForClaimAsync(first, job);
		var b = await LoadForClaimAsync(second, job);

		a.Document.StartProcessing(DateTimeOffset.UtcNow);
		b.Document.StartProcessing(DateTimeOffset.UtcNow);
		await a.Repository.SaveChangesAsync(CancellationToken.None);

		await Assert.ThrowsAsync<DocsAssistant.Application.Common.ConcurrencyConflictException>(
			() => b.Repository.SaveChangesAsync(CancellationToken.None));
	}

	[Fact]
	public async Task DiscardUnsavedChanges_RestoresThePersistedState()
	{
		var tenantId = await CreateTenantAsync();
		var job = await UploadAsync(tenantId, "doc.txt", "Contenido para descartar cambios no guardados."u8.ToArray());
		await using var scope = _services.CreateAsyncScope();
		var (document, repository) = await LoadForClaimAsync(scope, job);
		document.StartProcessing(DateTimeOffset.UtcNow);
		await repository.SaveChangesAsync(CancellationToken.None);

		document.MarkReady(chunkCount: 99, DateTimeOffset.UtcNow);
		await repository.DiscardUnsavedChangesAsync(document, CancellationToken.None);

		Assert.Equal(DocumentStatus.Processing, document.Status);
		Assert.Equal(0, document.ChunkCount);
	}

	[Fact]
	public async Task ExpiredClaim_IsReleasedBackToPending()
	{
		var tenantId = await CreateTenantAsync();
		var job = await UploadAsync(tenantId, "doc.txt", "Documento cuyo worker murió a mitad de camino."u8.ToArray());
		await using (var scope = _services.CreateAsyncScope())
		{
			var (document, repository) = await LoadForClaimAsync(scope, job);
			document.StartProcessing(DateTimeOffset.UtcNow.AddHours(-1));
			await repository.SaveChangesAsync(CancellationToken.None);
		}

		await using (var scope = _services.CreateAsyncScope())
		{
			var released = await scope.ServiceProvider.GetRequiredService<IPendingDocumentSource>()
				.ReleaseExpiredClaimsAsync(TimeSpan.FromMinutes(15), maxAttempts: 3, CancellationToken.None);
			Assert.True(released >= 1);
		}

		var status = await WithTenantAsync(tenantId, (db, _) =>
			db.Documents.Where(d => d.Id == job.DocumentId).Select(d => d.Status).SingleAsync());
		Assert.Equal(DocumentStatus.Pending, status);
	}

	private async Task<TenantId> CreateTenantAsync()
	{
		await using var scope = _services.CreateAsyncScope();
		var result = await scope.ServiceProvider.GetRequiredService<CreateTenant>().HandleAsync(
			new CreateTenantRequest($"Tenant {Guid.NewGuid():N}", "soporte@example.com", ContentLanguage.Spanish, []),
			CancellationToken.None);
		Assert.True(result.IsSuccess, result.Error?.Message);
		Assert.StartsWith("pk_", result.Value.PublicKey, StringComparison.Ordinal);
		return new TenantId(result.Value.Id);
	}

	private async Task<IngestionJob> UploadAsync(TenantId tenantId, string fileName, byte[] content)
	{
		await using var scope = _services.CreateAsyncScope();
		var result = await scope.ServiceProvider.GetRequiredService<UploadDocument>()
			.HandleAsync(new UploadDocumentRequest(tenantId.Value, fileName, null, content, null), CancellationToken.None);
		Assert.True(result.IsSuccess, result.Error?.Message);
		return new IngestionJob(tenantId, new DocumentId(result.Value.Document.Id));
	}

	private async Task<ProcessingOutcome> ProcessAsync(IngestionJob job)
	{
		await using var scope = _services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<ProcessDocument>().HandleAsync(job, CancellationToken.None);
	}

	private static async Task<(Document Document, IDocumentRepository Repository)> LoadForClaimAsync(AsyncServiceScope scope, IngestionJob job)
	{
		scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(job.TenantId);
		var repository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
		var document = await repository.GetAsync(job.DocumentId, CancellationToken.None);
		return (document!, repository);
	}

	private async Task<T> WithTenantAsync<T>(TenantId tenantId, Func<DocsAssistantDbContext, CancellationToken, Task<T>> query)
	{
		var context = new TenantContext();
		context.Set(tenantId);
		await using var db = _postgres.CreateContext(context);
		return await query(db, CancellationToken.None);
	}

	private async Task<List<string>> TenantIdsMatchingAsync(TenantId tenantId, string sql)
	{
		await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
		await connection.OpenAsync();
		await using var command = new NpgsqlCommand(sql, connection);
		command.Parameters.AddWithValue("tenant", tenantId.Value);
		var headings = new List<string>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			headings.Add(reader.GetString(0));
		}

		return headings;
	}

	private async Task<string> NearestHeadingAsync(TenantId tenantId, float[] query)
	{
		var builder = new NpgsqlDataSourceBuilder(_postgres.ConnectionString);
		builder.UseVector();
		await using var source = builder.Build();
		await using var command = source.CreateCommand(
			"SELECT heading FROM chunks WHERE tenant_id = @tenant ORDER BY embedding <=> @query LIMIT 1");
		command.Parameters.AddWithValue("tenant", tenantId.Value);
		command.Parameters.AddWithValue("query", new Vector(query));
		return (string)(await command.ExecuteScalarAsync())!;
	}
}
