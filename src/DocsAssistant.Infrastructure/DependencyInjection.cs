using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Application.Features.Handoff;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Application.Features.Tenants;
using DocsAssistant.Infrastructure.Ai;
using DocsAssistant.Infrastructure.Ai.Embeddings;
using DocsAssistant.Infrastructure.Extraction;
using DocsAssistant.Infrastructure.Handoff;
using DocsAssistant.Infrastructure.Ingestion;
using DocsAssistant.Infrastructure.Persistence;
using DocsAssistant.Infrastructure.Web;
using Microsoft.Extensions.AI;
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
		AddOptions<RetrievalOptions>(services, configuration, RetrievalOptions.SectionName);
		AddOptions<ChatAnswerOptions>(services, configuration, ChatAnswerOptions.SectionName);
		AddOptions<ExtractiveOptions>(services, configuration, ExtractiveOptions.SectionName);
		AddOptions<AiOptions>(services, configuration, AiOptions.SectionName);
		AddOptions<HandoffOptions>(services, configuration, HandoffOptions.SectionName);
		AddOptions<EmailOptions>(services, configuration, EmailOptions.SectionName)
			.PostConfigure(email => email.UseMailpit(configuration.GetConnectionString(EmailOptions.MailpitConnectionName)));

		// Persistence
		services.AddScoped<ITenantRepository, TenantRepository>();
		services.AddScoped<IDocumentRepository, DocumentRepository>();
		services.AddScoped<IPendingDocumentSource, PendingDocumentSource>();
		services.AddScoped<IChunkSearch, PgChunkSearch>();
		services.AddScoped<IConversationRepository, ConversationRepository>();
		services.AddScoped<ILeadRepository, LeadRepository>();
		services.AddScoped<IPendingLeadSource, PendingLeadSource>();

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

		// Language model
		services.AddSingleton<IPromptLibrary, PromptLibrary>();
		services.AddSingleton(provider => ChatClientSetup.Create(provider.GetRequiredService<IOptions<AiOptions>>().Value));

		// Ingestion pipeline
		services.AddSingleton<ChannelIngestionQueue>();
		services.AddSingleton<IIngestionQueue>(provider => provider.GetRequiredService<ChannelIngestionQueue>());
		services.AddHostedService<IngestionWorker>();

		// Handoff: leads are stored in the request, notified in the background.
		services.AddSingleton<IHandoffNotifier, SmtpHandoffNotifier>();
		services.AddSingleton<ChannelLeadQueue>();
		services.AddSingleton<ILeadQueue>(provider => provider.GetRequiredService<ChannelLeadQueue>());
		services.AddHostedService<LeadNotificationWorker>();

		// Use cases
		services.AddScoped<CreateTenant>();
		services.AddScoped<RegisterDocument>();
		services.AddScoped<UploadDocument>();
		services.AddScoped<AddUrlDocument>();
		services.AddScoped<ListDocuments>();
		services.AddScoped<ProcessDocument>();
		services.AddScoped<RetrieveContext>();
		services.AddScoped<ExtractiveAnswerer>();
		services.AddScoped<AskQuestion>();
		services.AddScoped<ConversationLog>();
		services.AddScoped<RequestHandoff>();
		services.AddScoped<NotifyLead>();
		services.AddScoped<ListLeads>();

		return services;
	}

	/// <summary>Bound, validated with data annotations, and checked at startup — not on first use.</summary>
	private static OptionsBuilder<TOptions> AddOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string section)
		where TOptions : class =>
		services.AddOptions<TOptions>()
			.Bind(configuration.GetSection(section))
			.ValidateDataAnnotations()
			.ValidateOnStart();
}
