using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocsAssistant.Infrastructure.Persistence;

public static class DatabaseMigrator
{
	/// <summary>
	/// Applies pending migrations. Called at startup in Development and by the deployment job in
	/// production — never implicitly on every production start, where several replicas would race.
	/// </summary>
	public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
	{
		await using var scope = services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<DocsAssistantDbContext>();
		await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
	}
}
