using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Application.Features.Tenants;
using DocsAssistant.Infrastructure.Ai.Embeddings;
using DocsAssistant.Infrastructure.Extraction;
using DocsAssistant.Infrastructure.Ingestion;
using DocsAssistant.Infrastructure.Persistence;
using DocsAssistant.Infrastructure.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

		services.AddSingleton(TimeProvider.System);
		services.AddScoped<ITenantContext, TenantContext>();
		services.AddDbContext<DocsAssistantDbContext>(options => PersistenceSetup.Configure(options, connectionString));

		AddOptions<IngestionOptions>(services, configuration, IngestionOptions.SectionName);
		AddOptions<UrlFetchOptions>(services, configuration, UrlFetchOptions.SectionName);
		AddOptions<EmbeddingModelOptions>(services, configuration, EmbeddingModelOptions.SectionName);

		// Persistence
		services.AddScoped<ITenantRepository, TenantRepository>();
		services.AddScoped<IDocumentRepository, DocumentRepository>();
		services.AddScoped<IPendingDocumentSource, PendingDocumentSource>();

		// Extraction: one per format, selected by DocumentSourceType.
		services.AddSingleton<ITextExtractor, PdfTextExtractor>();
		services.AddSingleton<ITextExtractor, DocxTextExtractor>();
		services.AddSingleton<ITextExtractor, MarkdownTextExtractor>();
		services.AddSingleton<ITextExtractor, PlainTextExtractor>();
		services.AddSingleton<ITextExtractor, HtmlTextExtractor>();

		// Web and embeddings
		services.AddSingleton<IUrlFetcher, SafeUrlFetcher>();
		services.AddHttpClient<ModelFileProvider>()
			.ConfigureHttpClient((provider, client) =>
				client.Timeout = provider.GetRequiredService<IOptions<EmbeddingModelOptions>>().Value.DownloadTimeout);
		services.AddSingleton<IEmbedder, OnnxEmbedder>();

		// Ingestion pipeline
		services.AddSingleton<ChannelIngestionQueue>();
		services.AddSingleton<IIngestionQueue>(provider => provider.GetRequiredService<ChannelIngestionQueue>());
		services.AddHostedService<IngestionWorker>();

		// Use cases
		services.AddScoped<CreateTenant>();
		services.AddScoped<RegisterDocument>();
		services.AddScoped<UploadDocument>();
		services.AddScoped<AddUrlDocument>();
		services.AddScoped<ListDocuments>();
		services.AddScoped<ProcessDocument>();

		return services;
	}

	/// <summary>Bound, validated with data annotations, and checked at startup — not on first use.</summary>
	private static void AddOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string section)
		where TOptions : class =>
		services.AddOptions<TOptions>()
			.Bind(configuration.GetSection(section))
			.ValidateDataAnnotations()
			.ValidateOnStart();
}
