using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocsAssistant.Api.Admin;
using DocsAssistant.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The API as deployed: real host, real worker, real database, real model. An upload goes in over
/// HTTP and the test waits for the background worker to report it Ready.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class AdminApiTests : IDisposable
{
	private const string AdminKey = "test-admin-key-0123456789abcdef-0123456789";

	/// <summary>Model already warm (EmbedderFixture); a small document is ready in well under this.</summary>
	private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

	private readonly WebApplicationFactory<Program> _factory;

	public AdminApiTests(PostgresFixture postgres)
	{
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", postgres.ConnectionString);
			builder.UseSetting($"{AdminOptions.SectionName}:ApiKey", AdminKey);
			builder.UseSetting("Ingestion:SweepInterval", "00:00:01");
		});
	}

	public void Dispose() => _factory.Dispose();

	private HttpClient Client(bool authenticated = true)
	{
		var client = _factory.CreateClient();
		if (authenticated)
		{
			client.DefaultRequestHeaders.Add(AdminOptions.HeaderName, AdminKey);
		}

		return client;
	}

	[Theory]
	[InlineData(null)]
	[InlineData("wrong-key")]
	[InlineData("test-admin-key-0123456789abcdef-012345678X")]
	public async Task AdminEndpoints_RequireTheKey(string? key)
	{
		using var client = Client(authenticated: false);
		if (key is not null)
		{
			client.DefaultRequestHeaders.Add(AdminOptions.HeaderName, key);
		}

		var response = await client.PostAsJsonAsync("/v1/admin/tenants", new { name = "x", handoffEmail = "a@b.co" });

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Upload_IsProcessedByTheWorkerUntilReady()
	{
		using var client = Client();
		var tenantId = await CreateTenantAsync(client);

		using var form = new MultipartFormDataContent();
		var file = new ByteArrayContent("# Envíos\n\nLos envíos son gratuitos desde 50 dólares y tardan de dos a cinco días hábiles."u8.ToArray());
		file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
		form.Add(file, "file", "envios.md");
		form.Add(new StringContent("Política de envíos"), "title");

		var upload = await client.PostAsync($"/v1/admin/tenants/{tenantId}/documents", form);
		Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);

		var document = await WaitForStatusAsync(client, tenantId, "Ready");
		Assert.Equal("Política de envíos", document.GetProperty("title").GetString());
		Assert.Equal("Markdown", document.GetProperty("format").GetString());
		Assert.True(document.GetProperty("chunkCount").GetInt32() >= 1);
	}

	[Fact]
	public async Task Upload_ToUnknownTenant_Is404WithAProblemCode()
	{
		using var client = Client();
		using var form = new MultipartFormDataContent { { new ByteArrayContent("hola"u8.ToArray()), "file", "a.txt" } };

		var response = await client.PostAsync($"/v1/admin/tenants/{Guid.NewGuid()}/documents", form);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("tenant.not_found", problem.GetProperty("code").GetString());
	}

	[Fact]
	public async Task AddUrl_PointingAtCloudMetadata_IsRejected()
	{
		using var client = Client();
		var tenantId = await CreateTenantAsync(client);

		var response = await client.PostAsJsonAsync(
			$"/v1/admin/tenants/{tenantId}/documents/url",
			new { url = "http://169.254.169.254/latest/meta-data/iam/security-credentials/" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("url.blocked", problem.GetProperty("code").GetString());
	}

	[Fact]
	public async Task CreateTenant_WithInvalidOrigin_Is400()
	{
		using var client = Client();

		var response = await client.PostAsJsonAsync(
			"/v1/admin/tenants",
			new { name = "Acme", handoffEmail = "help@acme.test", defaultLanguage = "English", allowedOrigins = new[] { "not-an-origin" } },
			Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	private static async Task<Guid> CreateTenantAsync(HttpClient client)
	{
		var response = await client.PostAsJsonAsync(
			"/v1/admin/tenants",
			new { name = $"Tenant {Guid.NewGuid():N}", handoffEmail = "soporte@example.com", defaultLanguage = "Spanish", allowedOrigins = new[] { "https://example.com" } },
			Json);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var tenant = await response.Content.ReadFromJsonAsync<JsonElement>();
		return tenant.GetProperty("id").GetGuid();
	}

	private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid tenantId, string status)
	{
		var deadline = DateTime.UtcNow + ReadyTimeout;
		var poll = TimeSpan.FromMilliseconds(250);
		JsonElement last = default;
		while (DateTime.UtcNow < deadline)
		{
			var documents = await client.GetFromJsonAsync<JsonElement>($"/v1/admin/tenants/{tenantId}/documents");
			last = documents[0];
			if (last.GetProperty("status").GetString() == status)
			{
				return last;
			}

			await Task.Delay(poll);
		}

		throw new TimeoutException($"Document never reached {status}. Last seen: {last}");
	}
}
