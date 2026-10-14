using System.Text.Json;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Application.Features.Tenants;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Persistence;
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

	private EvalEnvironment(ServiceProvider services, PostgreSqlContainer? container)
	{
		Services = services;
		_container = container;
	}

	public ServiceProvider Services { get; }

	public static async Task<EvalEnvironment> StartAsync(string? connectionString)
	{
		PostgreSqlContainer? container = null;
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			container = new PostgreSqlBuilder(PgvectorImage).Build();
			await container.StartAsync();
			connectionString = container.GetConnectionString();
		}

		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}"] = connectionString,
			})
			// Lets an experiment override any option without code changes, e.g. Retrieval__TextWeight=0.5.
			.AddEnvironmentVariables()
			.Build();
		var services = new ServiceCollection()
			.AddLogging(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
			.AddInfrastructure(configuration)
			.BuildServiceProvider();
		await DatabaseMigrator.MigrateAsync(services);
		return new EvalEnvironment(services, container);
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
				languages[Path.GetFileName(file)] = document!.Language;
				Console.WriteLine($"  ingested {Path.GetFileName(file),-28} {document.ChunkCount,4} chunks  {document.Language}");
			}
		}

		return (tenant, languages);
	}

	public async ValueTask DisposeAsync()
	{
		await Services.DisposeAsync();
		if (_container is not null)
		{
			await _container.DisposeAsync();
		}
	}
}

internal static class Thresholds
{
	public static async Task<IReadOnlyDictionary<string, double>> LoadAsync(string path)
	{
		await using var stream = File.OpenRead(path);
		var document = await JsonDocument.ParseAsync(stream);
		return document.RootElement.GetProperty("retrieval").EnumerateObject()
			.ToDictionary(p => p.Name, p => p.Value.GetDouble());
	}
}
