using DocsAssistant.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocsAssistant.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef migrations add`. Generating a migration never opens a connection, so the
/// connection string is a placeholder rather than a secret.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocsAssistantDbContext>
{
	private const string PlaceholderConnectionString = "Host=localhost;Database=docs_assistant_design";

	public DocsAssistantDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<DocsAssistantDbContext>();
		PersistenceSetup.Configure(options, PlaceholderConnectionString);
		return new DocsAssistantDbContext(options.Options, new TenantContext());
	}
}
