using Microsoft.EntityFrameworkCore;

namespace DocsAssistant.Infrastructure.Persistence;

internal static class PersistenceSetup
{
	public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
		options
			.UseNpgsql(connectionString, npgsql => npgsql.UseVector())
			.UseSnakeCaseNamingConvention();
}
