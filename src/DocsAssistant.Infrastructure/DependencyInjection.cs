using DocsAssistant.Application.Abstractions;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocsAssistant.Infrastructure;

public static class DependencyInjection
{
	/// <summary>Connection string name. Aspire injects it as ConnectionStrings__docsdb.</summary>
	public const string DatabaseConnectionName = "docsdb";

	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
			?? throw new InvalidOperationException(
				$"Missing connection string '{DatabaseConnectionName}'. Run through the AppHost, or set "
				+ $"ConnectionStrings__{DatabaseConnectionName}.");

		services.AddScoped<ITenantContext, TenantContext>();
		services.AddDbContext<DocsAssistantDbContext>(options => PersistenceSetup.Configure(options, connectionString));
		services.AddSingleton(TimeProvider.System);

		return services;
	}
}
