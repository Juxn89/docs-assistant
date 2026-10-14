using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DocsAssistant.Api.Admin;
using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The public chat endpoint end to end — real ingestion, real retrieval, real SSE — with only the
/// language model replaced. What the model would say is not under test here; what the system does
/// around it (origin checks, streaming, citations, failure as a typed event) is.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class ChatApiTests : IDisposable
{
	private const string AdminKey = "test-admin-key-0123456789abcdef-0123456789";
	private const string AllowedOrigin = "https://shop.example.com";
	private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly PostgresFixture _postgres;
	private readonly List<WebApplicationFactory<Program>> _factories = [];

	public ChatApiTests(PostgresFixture postgres) => _postgres = postgres;

	public void Dispose()
	{
		foreach (var factory in _factories)
		{
			factory.Dispose();
		}
	}

	private HttpClient Client(IChatClient model, bool extractiveFallback = true)
	{
		var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", _postgres.ConnectionString);
			builder.UseSetting($"{AdminOptions.SectionName}:ApiKey", AdminKey);
			builder.UseSetting("Ingestion:SweepInterval", "00:00:01");
			// These tests poll the admin API while waiting for ingestion; rate limits are tested in RateLimitingTests.
			builder.UseSetting("RateLimiting:AdminPerMinutePerIp", "10000");
			builder.UseSetting("RateLimiting:ChatPerMinutePerIp", "10000");
			builder.UseSetting("Chat:ExtractiveFallback", extractiveFallback.ToString());
			builder.ConfigureTestServices(services => services.AddSingleton(model));
		});
		_factories.Add(factory);
		return factory.CreateClient();
	}

	[Fact]
	public async Task Question_StreamsTheAnswerAndResolvesCitations()
	{
		using var client = Client(new ScriptedModel("Returns are accepted for ", "30 days [1]."));
		var key = await SeedTenantAsync(client, "# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt.");

		var events = await AskAsync(client, key, "How many days do I have to return a product?", AllowedOrigin);

		Assert.Equal("Returns are accepted for 30 days [1].", string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString())));
		var done = Assert.Single(events, e => e.Name == "done").Data;
		Assert.Equal("Answered", done.GetProperty("outcome").GetString());
		Assert.False(done.GetProperty("offerHandoff").GetBoolean());
		var citation = Assert.Single(done.GetProperty("citations").EnumerateArray().ToList());
		Assert.Equal(1, citation.GetProperty("number").GetInt32());
		Assert.Equal("Returns", citation.GetProperty("section").GetString());
	}

	[Fact]
	public async Task OffTopicQuestion_AbstainsWithoutCallingTheModel()
	{
		var model = new ScriptedModel("should never be used");
		using var client = Client(model);
		var key = await SeedTenantAsync(client, "# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt.");

		var events = await AskAsync(client, key, "Who won the football world cup in 1998?", AllowedOrigin);

		var done = Assert.Single(events, e => e.Name == "done").Data;
		Assert.Equal("AbstainedNoContext", done.GetProperty("outcome").GetString());
		Assert.True(done.GetProperty("offerHandoff").GetBoolean());
		Assert.False(model.Called);
	}

	[Fact]
	public async Task UnknownKey_Is404()
	{
		using var client = Client(new ScriptedModel());

		var response = await client.PostAsJsonAsync("/v1/chat/pk_does_not_exist", new { question = "hi" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task MalformedBody_Is400NotA500()
	{
		using var client = Client(new ScriptedModel());
		var key = await SeedTenantAsync(client, "# Hours\n\nOpen Monday to Friday.");

		// "¿" in Windows-1252 (0xBF) is not valid UTF-8: what a misconfigured client really sent.
		var invalidUtf8 = new ByteArrayContent([.. "{\"question\":\""u8, 0xBF, .. "Hola?\"}"u8]);
		invalidUtf8.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		var response = await client.PostAsync($"/v1/chat/{key}", invalidUtf8);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task OriginNotRegisteredForTheTenant_Is403()
	{
		using var client = Client(new ScriptedModel());
		var key = await SeedTenantAsync(client, "# Hours\n\nOpen Monday to Friday.");

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/chat/{key}") { Content = JsonContent.Create(new { question = "When are you open?" }) };
		request.Headers.Add("Origin", "https://evil.example.net");
		var response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task NoModelConfigured_AnswersByQuotingThePassages()
	{
		using var client = Client(new UnconfiguredChatClient());
		var key = await SeedTenantAsync(client, "# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt.");

		var events = await AskAsync(client, key, "How many days do I have to return a product?", AllowedOrigin);

		var done = Assert.Single(events, e => e.Name == "done").Data;
		Assert.Equal("AnsweredExtractively", done.GetProperty("outcome").GetString());
		var answer = string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString()));
		Assert.Equal(
			"This is the most relevant information I found in the documents: Returns are accepted within 30 days of delivery with the original receipt. [1]",
			answer);
		Assert.True(done.GetProperty("offerHandoff").GetBoolean());
		Assert.DoesNotContain(events, e => e.Name == "error");
	}

	[Fact]
	public async Task NoModelAndFallbackDisabled_EndsTheStreamWithATypedError()
	{
		using var client = Client(new UnconfiguredChatClient(), extractiveFallback: false);
		var key = await SeedTenantAsync(client, "# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt.");

		var events = await AskAsync(client, key, "How many days do I have to return a product?", AllowedOrigin);

		var error = Assert.Single(events, e => e.Name == "error").Data;
		Assert.Equal("assistant.unavailable", error.GetProperty("code").GetString());
		Assert.DoesNotContain(events, e => e.Name == "done");
	}

	private static async Task<string> SeedTenantAsync(HttpClient client, string markdown)
	{
		client.DefaultRequestHeaders.Remove(AdminOptions.HeaderName);
		client.DefaultRequestHeaders.Add(AdminOptions.HeaderName, AdminKey);
		var created = await client.PostAsJsonAsync(
			"/v1/admin/tenants",
			new { name = $"Shop {Guid.NewGuid():N}", handoffEmail = "help@shop.example.com", defaultLanguage = "English", allowedOrigins = new[] { AllowedOrigin } });
		var tenant = await created.Content.ReadFromJsonAsync<JsonElement>();
		var tenantId = tenant.GetProperty("id").GetGuid();

		using var form = new MultipartFormDataContent();
		var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(markdown));
		file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
		form.Add(file, "file", "policy.md");
		(await client.PostAsync($"/v1/admin/tenants/{tenantId}/documents", form)).EnsureSuccessStatusCode();

		var deadline = DateTime.UtcNow + ReadyTimeout;
		while (DateTime.UtcNow < deadline)
		{
			var documents = await client.GetFromJsonAsync<JsonElement>($"/v1/admin/tenants/{tenantId}/documents");
			if (documents[0].GetProperty("status").GetString() == "Ready")
			{
				client.DefaultRequestHeaders.Remove(AdminOptions.HeaderName);
				return tenant.GetProperty("publicKey").GetString()!;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(250));
		}

		throw new TimeoutException("The seeded document never became Ready.");
	}

	private static async Task<List<SseEvent>> AskAsync(HttpClient client, string key, string question, string origin)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/chat/{key}") { Content = JsonContent.Create(new { question }) };
		request.Headers.Add("Origin", origin);
		using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		response.EnsureSuccessStatusCode();
		Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

		var body = await response.Content.ReadAsStringAsync();
		return
		[
			.. body.Replace("\r\n", "\n", StringComparison.Ordinal)
				.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
				.Select(block => block.Split('\n'))
				.Select(lines => new SseEvent(
					Field(lines, "event"),
					JsonSerializer.Deserialize<JsonElement>(Field(lines, "data"), Json))),
		];

		static string Field(string[] lines, string name) =>
			lines.Single(l => l.StartsWith(name + ":", StringComparison.Ordinal))[(name.Length + 1)..].Trim();
	}

	private sealed record SseEvent(string Name, JsonElement Data);

	private sealed class ScriptedModel(params string[] chunks) : IChatClient
	{
		public bool Called { get; private set; }

		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<ChatMessage> messages,
			ChatOptions? options = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			Called = true;
			foreach (var chunk in chunks)
			{
				await Task.Yield();
				yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
			}
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}
}
