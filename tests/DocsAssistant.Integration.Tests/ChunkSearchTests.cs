using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;
using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The search SQL is raw, so it does not get EF's tenant filter for free. These tests pin the
/// properties that matter: tenant isolation, OR semantics for natural questions, and correct results
/// for a small tenant sharing an HNSW index with a large one.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class ChunkSearchTests : IDisposable
{
	private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
	private readonly PostgresFixture _postgres;
	private readonly ServiceProvider _services;

	public ChunkSearchTests(PostgresFixture postgres)
	{
		_postgres = postgres;
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}"] = postgres.ConnectionString,
			})
			.Build();
		_services = new ServiceCollection()
			.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
			.AddInfrastructure(configuration)
			.BuildServiceProvider();
	}

	public void Dispose() => _services.Dispose();

	[Fact]
	public async Task Search_NeverReturnsAnotherTenantsChunks()
	{
		const string Text = "Las devoluciones se aceptan dentro de los treinta días.";
		var vector = RandomUnitVector(seed: 1);
		var alice = await SeedTenantAsync(ContentLanguage.Spanish, (Text, vector));
		var bob = await SeedTenantAsync(ContentLanguage.Spanish, (Text, vector));

		await using var scope = BoundScope(bob);
		var search = scope.ServiceProvider.GetRequiredService<IChunkSearch>();
		var byVector = await search.SearchByVectorAsync(vector, 10, CancellationToken.None);
		var byText = await search.SearchByTextAsync("devoluciones", 10, CancellationToken.None);

		var bobChunks = await ChunkIdsOfAsync(bob);
		var aliceChunks = await ChunkIdsOfAsync(alice);
		Assert.All(byVector.Concat(byText), hit => Assert.Contains(hit.ChunkId, bobChunks));
		Assert.DoesNotContain(byVector.Concat(byText), hit => aliceChunks.Contains(hit.ChunkId));
		Assert.Single(byVector);
		Assert.Single(byText);
	}

	[Fact]
	public async Task Search_WithoutATenant_Refuses()
	{
		await using var scope = _services.CreateAsyncScope();
		var search = scope.ServiceProvider.GetRequiredService<IChunkSearch>();

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => search.SearchByTextAsync("anything", 5, CancellationToken.None));
	}

	[Fact]
	public async Task TextSearch_MatchesAnyMeaningfulWord_InEitherLanguage()
	{
		// Each document is indexed with its own language's stemmer, as ingestion does after detection.
		var englishTenant = await SeedTenantAsync(ContentLanguage.English, ("Returns are processed within three business days.", RandomUnitVector(2)));
		var spanishTenant = await SeedTenantAsync(ContentLanguage.Spanish, ("Nuestras oficinas abren de lunes a viernes.", RandomUnitVector(3)));

		await using var englishScope = BoundScope(englishTenant);
		await using var spanishScope = BoundScope(spanishTenant);
		var inEnglish = englishScope.ServiceProvider.GetRequiredService<IChunkSearch>();
		var inSpanish = spanishScope.ServiceProvider.GetRequiredService<IChunkSearch>();

		// A natural question shares one stem with the chunk ("returns"), not every word.
		Assert.Single(await inEnglish.SearchByTextAsync("how long do returns take?", 5, CancellationToken.None));
		Assert.Single(await inSpanish.SearchByTextAsync("¿qué días abren las oficinas?", 5, CancellationToken.None));

		// Spanish stop words carry no meaning and must not match a Spanish document.
		Assert.Empty(await inSpanish.SearchByTextAsync("¿y el de la?", 5, CancellationToken.None));
	}

	[Fact]
	public async Task VectorSearch_DominantTenant_QueryNearAnotherTenantsData_StillFillsTheLimit()
	{
		// Pins the result, not the path: the dominant tenant must get its own nearest chunks even when the
		// question sits next to another tenant's data. Which plan serves it (btree + sort, HNSW with
		// iterative scan, or the exact fallback) is the planner's choice, and with 384 dimensions it did
		// not need the fallback in this suite. The failure the fallback guards against was reproduced in a
		// manual probe and is recorded in ADR-0002; it is not reproducible here without rigging the planner.
		const int DominantChunks = 2000;
		const int OtherChunks = 400;
		const int Limit = 5;
		var direction = RandomUnitVector(seed: 42);
		var dominant = await SeedTenantAsync(
			ContentLanguage.English,
			[.. Enumerable.Range(0, DominantChunks).Select(i => ($"dominant {i}", Around(direction, seed: 10_000 + i)))]);
		await SeedTenantAsync(
			ContentLanguage.English,
			[.. Enumerable.Range(0, OtherChunks).Select(i => ($"other {i}", Around(Negate(direction), seed: 20_000 + i)))]);
		var query = Around(Negate(direction), seed: 99);

		await using var scope = BoundScope(dominant);
		var hits = await scope.ServiceProvider.GetRequiredService<IChunkSearch>()
			.SearchByVectorAsync(query, Limit, CancellationToken.None);

		Assert.Equal(Limit, hits.Count);
		var dominantChunks = await ChunkIdsOfAsync(dominant);
		Assert.All(hits, hit => Assert.Contains(hit.ChunkId, dominantChunks));
	}

	[Fact]
	public async Task VectorSearch_TenantWithFewerChunksThanTheLimit_ReturnsAllOfThem()
	{
		var tenant = await SeedTenantAsync(ContentLanguage.English, ("one", RandomUnitVector(5)), ("two", RandomUnitVector(6)));

		await using var scope = BoundScope(tenant);
		var hits = await scope.ServiceProvider.GetRequiredService<IChunkSearch>()
			.SearchByVectorAsync(RandomUnitVector(7), 5, CancellationToken.None);

		Assert.Equal(2, hits.Count);
	}

	[Fact]
	public async Task Retrieve_LowSimilarity_IsNotConfident()
	{
		var tenant = await SeedTenantAsync(ContentLanguage.English, ("unrelated text", RandomUnitVector(7)));

		await using var scope = _services.CreateAsyncScope();
		var result = await scope.ServiceProvider.GetRequiredService<RetrieveContext>()
			.HandleAsync(tenant, "What is the capital of France?", CancellationToken.None);

		// The seeded vector is random, so its similarity to any real question is near zero.
		Assert.False(result.IsConfident);
	}

	private AsyncServiceScope BoundScope(TenantId tenant)
	{
		var scope = _services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
		return scope;
	}

	private async Task<TenantId> SeedTenantAsync(ContentLanguage language, params (string Text, float[] Vector)[] chunks)
	{
		var tenant = Tenant.Create($"t-{Guid.NewGuid():N}", $"pk_{Guid.NewGuid():N}", "a@b.co", language, [], Now);
		var context = new TenantContext();
		context.Set(tenant.Id);
		await using var db = _postgres.CreateContext(context);

		var document = Document.Register(
			tenant.Id, "seed", DocumentSourceType.PlainText, "seed.txt", ContentHash.Compute(Guid.NewGuid().ToByteArray()), language, Now);
		document.StartProcessing(Now);
		document.MarkReady(chunks.Length, Now);

		db.Tenants.Add(tenant);
		db.Documents.Add(document);
		db.Chunks.AddRange(chunks.Select((c, i) =>
			Chunk.Create(document, new ChunkDraft(i, null, c.Text, TokenEstimator.Estimate(c.Text)), c.Vector)));
		await db.SaveChangesAsync();
		return tenant.Id;
	}

	private async Task<HashSet<ChunkId>> ChunkIdsOfAsync(TenantId tenant)
	{
		var context = new TenantContext();
		context.Set(tenant);
		await using var db = _postgres.CreateContext(context);
		return [.. db.Chunks.Select(c => c.Id)];
	}

	private static float[] RandomUnitVector(int seed)
	{
		var random = new Random(seed);
		var vector = new float[PostgresNames.EmbeddingDimensions];
		for (var i = 0; i < vector.Length; i++)
		{
			vector[i] = (float)(random.NextDouble() - 0.5);
		}

		var norm = MathF.Sqrt(vector.Sum(x => x * x));
		return [.. vector.Select(x => x / norm)];
	}

	private static float[] Negate(float[] vector) => [.. vector.Select(x => -x)];

	/// <summary>A unit vector close to <paramref name="center"/>: a cluster member.</summary>
	private static float[] Around(float[] center, int seed)
	{
		const float Spread = 0.05f;
		var noise = RandomUnitVector(seed);
		var vector = center.Select((x, i) => x + (Spread * noise[i])).ToArray();
		var norm = MathF.Sqrt(vector.Sum(x => x * x));
		return [.. vector.Select(x => x / norm)];
	}
}
