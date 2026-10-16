using System.Text.Json;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Application.Features.Tenants;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Ai;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace DocsAssistant.Evals;

internal sealed record RepoPaths(string Root)
{
	public string CorpusDirectory => Path.Combine(Root, "evals", "corpus");

	public string DatasetsDirectory => Path.Combine(Root, "evals", "datasets");

	public string ResultsDirectory => Path.Combine(Root, "evals", "results");

	public string ThresholdsFile => Path.Combine(Root, "evals", "thresholds.json");

	public string PricingFile => Path.Combine(Root, "evals", "pricing.json");

	public static RepoPaths Locate()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "DocsAssistant.slnx")))
			{
				return new RepoPaths(directory.FullName);
			}
		}

		throw new InvalidOperationException("Run the evals from inside the repository.");
	}
}

/// <summary>
/// The production pipeline (AddInfrastructure) against a real Postgres. Evals that used a different
/// wiring than the API would measure something nobody runs.
/// </summary>
internal sealed class EvalEnvironment : IAsyncDisposable
{
	private const string PgvectorImage = "pgvector/pgvector:0.8.7-pg18";

	private readonly PostgreSqlContainer? _container;
	private readonly OllamaSandbox? _ollama;

	private EvalEnvironment(ServiceProvider services, PostgreSqlContainer? container, OllamaSandbox? ollama)
	{
		Services = services;
		_container = container;
		_ollama = ollama;
	}

	public ServiceProvider Services { get; }

	/// <param name="settings">Options the run imposes over the environment (a paid run turns the extractive fallback off).</param>
	/// <param name="wrapChat">Replaces the model client, e.g. to meter it or to stand in for it in a dry run.</param>
	public static async Task<EvalEnvironment> StartAsync(
		string? connectionString,
		IReadOnlyDictionary<string, string?>? settings = null,
		Func<IServiceProvider, IChatClient, IChatClient>? wrapChat = null)
	{
		PostgreSqlContainer? container = null;
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			container = new PostgreSqlBuilder(PgvectorImage).Build();
			await container.StartAsync();
			connectionString = container.GetConnectionString();
		}

		// Ai__Provider=Ollama without an explicit Ai__OllamaEndpoint: start a sandboxed Ollama container
		// for this run. With an endpoint (e.g. the deploy/ollama compose stack), use that instead.
		var requested = new ConfigurationBuilder().AddEnvironmentVariables().Build();
		OllamaSandbox? ollama = null;
		var overrides = new Dictionary<string, string?>();
		if (string.Equals(requested["Ai:Provider"], nameof(ChatProvider.Ollama), StringComparison.OrdinalIgnoreCase)
			&& string.IsNullOrWhiteSpace(requested["Ai:OllamaEndpoint"]))
		{
			ollama = await OllamaSandbox.StartAsync(requested["Ai:OllamaModel"] ?? new AiOptions().OllamaModel);
			overrides["Ai:OllamaEndpoint"] = ollama.Endpoint.ToString();
		}

		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}"] = connectionString,
			})
			// Lets an experiment override any option without code changes, e.g. Retrieval__TextWeight=0.5.
			.AddEnvironmentVariables()
			.AddInMemoryCollection(overrides)
			.AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
			.Build();
		var collection = new ServiceCollection()
			.AddLogging(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
			.AddInfrastructure(configuration);
		if (wrapChat is not null)
		{
			var original = collection.Last(d => d.ServiceType == typeof(IChatClient));
			collection.Remove(original);
			collection.AddSingleton(provider => wrapChat(provider, (IChatClient)original.ImplementationFactory!(provider)));
		}

		var services = collection.BuildServiceProvider();
		await DatabaseMigrator.MigrateAsync(services);
		return new EvalEnvironment(services, container, ollama);
	}

	/// <summary>Creates a tenant and ingests every corpus file through the real use cases, synchronously.</summary>
	public async Task<(TenantId Tenant, IReadOnlyDictionary<string, ContentLanguage> Languages)> IngestCorpusAsync(string corpusDirectory)
	{
		TenantId tenant;
		await using (var scope = Services.CreateAsyncScope())
		{
			var created = await scope.ServiceProvider.GetRequiredService<CreateTenant>().HandleAsync(
				new CreateTenantRequest($"evals-{DateTime.UtcNow:yyyyMMddHHmmss}", "evals@example.com", ContentLanguage.English, []),
				CancellationToken.None);
			tenant = new TenantId(created.Value.Id);
		}

		var languages = new Dictionary<string, ContentLanguage>(StringComparer.OrdinalIgnoreCase);
		foreach (var file in Directory.EnumerateFiles(corpusDirectory, "*.md").Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase)).Order())
		{
			languages[Path.GetFileName(file)] = await IngestFileAsync(tenant, file);
		}

		return (tenant, languages);
	}

	/// <summary>Uploads and processes one file for the tenant through the real use cases. Returns its detected language.</summary>
	public async Task<ContentLanguage> IngestFileAsync(TenantId tenant, string file)
	{
		IngestionJob job;
		await using (var scope = Services.CreateAsyncScope())
		{
			var registered = await scope.ServiceProvider.GetRequiredService<UploadDocument>().HandleAsync(
				new UploadDocumentRequest(tenant.Value, Path.GetFileName(file), "text/markdown", await File.ReadAllBytesAsync(file), null),
				CancellationToken.None);
			if (!registered.IsSuccess)
			{
				throw new InvalidOperationException($"{file}: {registered.Error!.Message}");
			}

			job = new IngestionJob(tenant, new DocumentId(registered.Value.Document.Id));
		}

		await using (var scope = Services.CreateAsyncScope())
		{
			var outcome = await scope.ServiceProvider.GetRequiredService<ProcessDocument>().HandleAsync(job, CancellationToken.None);
			if (outcome != ProcessingOutcome.Ready)
			{
				throw new InvalidOperationException($"{file} did not ingest: {outcome}.");
			}
		}

		await using (var scope = Services.CreateAsyncScope())
		{
			scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
			var document = await scope.ServiceProvider.GetRequiredService<IDocumentRepository>().GetAsync(job.DocumentId, CancellationToken.None);
			Console.WriteLine($"  ingested {Path.GetFileName(file),-28} {document!.ChunkCount,4} chunks  {document.Language}");
			return document.Language;
		}
	}

	public async ValueTask DisposeAsync()
	{
		await Services.DisposeAsync();
		if (_container is not null)
		{
			await _container.DisposeAsync();
		}

		if (_ollama is not null)
		{
			await _ollama.DisposeAsync();
		}
	}
}

internal static class Thresholds
{
	public static async Task<IReadOnlyDictionary<string, double>> LoadAsync(string path, string section = "retrieval")
	{
		await using var stream = File.OpenRead(path);
		var document = await JsonDocument.ParseAsync(stream);
		return document.RootElement.GetProperty(section).EnumerateObject()
			.ToDictionary(p => p.Name, p => p.Value.GetDouble());
	}
}

/// <summary>Minimums by default; a key ending in ".max" is a ceiling (error rates, truncated answers).</summary>
internal static class ThresholdCheck
{
	private const string MaxSuffix = ".max";

	public static IReadOnlyList<string> Failures(IReadOnlyDictionary<string, double> actual, IReadOnlyDictionary<string, double> thresholds) =>
	[
		.. thresholds
			.Where(t => !actual.TryGetValue(t.Key, out var value)
				|| (t.Key.EndsWith(MaxSuffix, StringComparison.Ordinal) ? value > t.Value : value < t.Value))
			.Select(t => string.Create(
				System.Globalization.CultureInfo.InvariantCulture,
				$"{t.Key} = {actual.GetValueOrDefault(t.Key):F3} {(t.Key.EndsWith(MaxSuffix, StringComparison.Ordinal) ? ">" : "<")} {t.Value:F3}")),
	];
}
