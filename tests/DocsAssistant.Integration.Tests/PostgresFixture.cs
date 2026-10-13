using DocsAssistant.Application.Abstractions;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// One real Postgres + pgvector per test collection, migrated once. Same image tag as the AppHost,
/// so the tests exercise the extension version that runs locally and in CI.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
	public const string Image = "pgvector/pgvector:0.8.7-pg18";

	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

	public string ConnectionString => _container.GetConnectionString();

	public async Task InitializeAsync()
	{
		await _container.StartAsync();
		await using var db = CreateContext(new TenantContext());
		await db.Database.MigrateAsync();
	}

	public Task DisposeAsync() => _container.DisposeAsync().AsTask();

	public DocsAssistantDbContext CreateContext(ITenantContext tenantContext)
	{
		var options = new DbContextOptionsBuilder<DocsAssistantDbContext>();
		PersistenceSetup.Configure(options, ConnectionString);
		return new DocsAssistantDbContext(options.Options, tenantContext);
	}
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
	public const string Name = "postgres";
}
