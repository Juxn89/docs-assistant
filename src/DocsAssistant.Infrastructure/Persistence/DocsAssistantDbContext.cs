using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace DocsAssistant.Infrastructure.Persistence;

public sealed class DocsAssistantDbContext(
	DbContextOptions<DocsAssistantDbContext> options,
	ITenantContext tenantContext) : DbContext(options)
{
	public DbSet<Tenant> Tenants => Set<Tenant>();

	public DbSet<Document> Documents => Set<Document>();

	public DbSet<Chunk> Chunks => Set<Chunk>();

	/// <summary>
	/// Read by the query filters at execution time (EF parameterizes members of the context), so one
	/// compiled model serves every tenant. With no tenant in context it is the empty GUID, which no
	/// version 7 id can equal — the filter fails closed.
	/// </summary>
	internal TenantId CurrentTenantFilter => tenantContext.CurrentTenantId ?? default;

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		ArgumentNullException.ThrowIfNull(modelBuilder);

		modelBuilder.HasPostgresExtension(PostgresNames.VectorExtension);
		modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocsAssistantDbContext).Assembly);

		modelBuilder.Entity<Document>().HasQueryFilter(d => d.TenantId == CurrentTenantFilter);
		modelBuilder.Entity<Chunk>().HasQueryFilter(c => c.TenantId == CurrentTenantFilter);
	}

	protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
	{
		ArgumentNullException.ThrowIfNull(configurationBuilder);

		configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
		configurationBuilder.Properties<DocumentId>().HaveConversion<DocumentIdConverter>();
		configurationBuilder.Properties<ChunkId>().HaveConversion<ChunkIdConverter>();
		configurationBuilder.Properties<ContentHash>().HaveConversion<ContentHashConverter>();
	}
}
