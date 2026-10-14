using DocsAssistant.Infrastructure.Ai.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The real model, loaded once per test run. The first run downloads ~123 MB into the user cache
/// (CI caches that directory); every later run verifies the hashes and reuses the files.
/// </summary>
public sealed class EmbedderFixture : IAsyncLifetime, IDisposable
{
	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

	internal OnnxEmbedder Embedder { get; private set; } = null!;

	internal XlmRobertaTokenizer Tokenizer { get; private set; } = null!;

	internal EmbeddingModelOptions Options { get; } = new();

	public async Task InitializeAsync()
	{
		var files = new ModelFileProvider(_http, NullLogger<ModelFileProvider>.Instance);
		Embedder = new OnnxEmbedder(Microsoft.Extensions.Options.Options.Create(Options), files);

		// Forces download + load once, so individual tests measure behaviour rather than setup.
		await Embedder.EmbedQueryAsync("warm up", CancellationToken.None);

		var tokenizerPath = await files.EnsureAsync(Options.TokenizerUrl, Options.TokenizerSha256, Options.ResolveCacheDirectory(), CancellationToken.None);
		await using var stream = File.OpenRead(tokenizerPath);
		Tokenizer = new XlmRobertaTokenizer(stream);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		Embedder.Dispose();
		_http.Dispose();
	}
}

[CollectionDefinition(Name)]
public sealed class EmbedderCollection : ICollectionFixture<EmbedderFixture>
{
	public const string Name = "embedder";
}
