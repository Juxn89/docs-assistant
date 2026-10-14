using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Handoff end to end: a visitor chats, asks for a person, and the operator receives an email in a
/// real SMTP server (Mailpit) with the conversation in it. No model: answers are extractive.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class HandoffApiTests : IClassFixture<MailpitFixture>, IDisposable
{
	private const string AdminKey = "test-admin-key-0123456789abcdef-0123456789";
	private const string AllowedOrigin = "https://shop.example.com";
	private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(30);
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly PostgresFixture _postgres;
	private readonly MailpitFixture _mail;
	private readonly List<WebApplicationFactory<Program>> _factories = [];

	public HandoffApiTests(PostgresFixture postgres, MailpitFixture mail)
	{
		_postgres = postgres;
		_mail = mail;
	}

	public void Dispose()
	{
		foreach (var factory in _factories)
		{
			factory.Dispose();
		}
	}

	/// <param name="smtpPort">Defaults to Mailpit; a closed port simulates a mail server that is down.</param>
	private HttpClient Client(int? smtpPort = null)
	{
		var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", _postgres.ConnectionString);
			builder.UseSetting($"{AdminOptions.SectionName}:ApiKey", AdminKey);
			builder.UseSetting("Ingestion:SweepInterval", "00:00:01");
			builder.UseSetting("Handoff:SweepInterval", "00:00:01");
			builder.UseSetting("Handoff:RetryDelay", "00:00:01");
			builder.UseSetting("Email:Host", _mail.SmtpHost);
			builder.UseSetting("Email:Port", (smtpPort ?? _mail.SmtpPortOnHost).ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.UseSetting("Email:Security", "None");
			builder.UseSetting("Email:Timeout", "00:00:05");
			builder.UseSetting("RateLimiting:AdminPerMinutePerIp", "10000");
			builder.UseSetting("RateLimiting:ChatPerMinutePerIp", "10000");
			builder.UseSetting("RateLimiting:HandoffPerHourPerIp", "10000");
			builder.ConfigureTestServices(services => services.AddSingleton<IChatClient>(new UnconfiguredChatClient()));
		});
		_factories.Add(factory);
		return factory.CreateClient();
	}

	[Fact]
	public async Task VisitorChatsThenAsksForAPerson_OperatorGetsTheConversationByEmail()
	{
		using var client = Client();
		var shop = await SeedTenantAsync(client);

		var first = await AskAsync(client, shop.Key, "How many days do I have to return a product?", conversationId: null);
		var second = await AskAsync(client, shop.Key, "Do you ship to Guatemala?", first);
		Assert.Equal(first, second);

		var response = await PostHandoffAsync(client, shop.Key, new { conversationId = first, email = "ana@example.com", name = "Ana López", message = "I need a quote for 200 units." });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

		var email = await _mail.WaitForEmailToAsync(shop.HandoffEmail, MailTimeout);
		Assert.Equal("ana@example.com", email.GetProperty("ReplyTo")[0].GetProperty("Address").GetString());
		Assert.Equal("Ana López", email.GetProperty("ReplyTo")[0].GetProperty("Name").GetString());
		var text = email.GetProperty("Text").GetString()!;
		Assert.Contains("I need a quote for 200 units.", text, StringComparison.Ordinal);
		Assert.Contains("How many days do I have to return a product?", text, StringComparison.Ordinal);
		Assert.Contains("Do you ship to Guatemala?", text, StringComparison.Ordinal);
		Assert.Contains("Returns are accepted within 30 days", text, StringComparison.Ordinal);
		Assert.Contains("— policy.md", text, StringComparison.Ordinal);
		Assert.True(
			text.IndexOf("How many days", StringComparison.Ordinal) < text.IndexOf("Do you ship", StringComparison.Ordinal),
			"The transcript reads oldest first.");

		var lead = await WaitForLeadStatusAsync(client, shop.TenantId, "Notified");
		Assert.Equal(first, lead.GetProperty("conversationId").GetGuid());
	}

	[Fact]
	public async Task MailServerDown_TheLeadIsKeptAndListed_WithTheError()
	{
		using var client = Client(smtpPort: 1);
		var shop = await SeedTenantAsync(client);

		var response = await PostHandoffAsync(client, shop.Key, new { email = "ana@example.com", message = "Call me back." });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

		var deadline = DateTime.UtcNow + MailTimeout;
		JsonElement lead;
		do
		{
			lead = (await ListLeadsAsync(client, shop.TenantId)).Single();
			await Task.Delay(TimeSpan.FromMilliseconds(250));
		}
		while ((lead.GetProperty("attempts").GetInt32() == 0 || lead.GetProperty("status").GetString() == "Sending") && DateTime.UtcNow < deadline);

		Assert.NotEqual("Notified", lead.GetProperty("status").GetString());
		Assert.True(lead.GetProperty("attempts").GetInt32() >= 1);
		Assert.False(string.IsNullOrEmpty(lead.GetProperty("lastError").GetString()));
		Assert.Equal(0, await _mail.CountEmailsToAsync(shop.HandoffEmail));
	}

	[Fact]
	public async Task AnotherAssistantsConversationId_IsNotAttached()
	{
		using var client = Client();
		var shopA = await SeedTenantAsync(client);
		var shopB = await SeedTenantAsync(client);
		var conversationOfA = await AskAsync(client, shopA.Key, "How many days do I have to return a product?", conversationId: null);

		await PostHandoffAsync(client, shopB.Key, new { conversationId = conversationOfA, email = "eve@example.com", message = "Show me what A's visitor asked." });

		var email = await _mail.WaitForEmailToAsync(shopB.HandoffEmail, MailTimeout);
		Assert.DoesNotContain("How many days", email.GetProperty("Text").GetString(), StringComparison.Ordinal);
		var lead = (await ListLeadsAsync(client, shopB.TenantId)).Single();
		Assert.Equal(JsonValueKind.Null, lead.GetProperty("conversationId").ValueKind);
	}

	[Theory]
	[InlineData("not-an-email", "Hi", "handoff.invalid_email")]
	[InlineData("ana@example.com", "", "handoff.message_required")]
	public async Task InvalidRequest_Is400WithACode(string email, string message, string code)
	{
		using var client = Client();
		var shop = await SeedTenantAsync(client);

		var response = await PostHandoffAsync(client, shop.Key, new { email, message });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(code, problem.GetProperty("code").GetString());
		Assert.Empty(await ListLeadsAsync(client, shop.TenantId));
	}

	[Fact]
	public async Task OriginNotRegistered_Is403_AndNoLeadIsCreated()
	{
		using var client = Client();
		var shop = await SeedTenantAsync(client);

		var response = await PostHandoffAsync(client, shop.Key, new { email = "ana@example.com", message = "Hi" }, origin: "https://evil.example.net");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Empty(await ListLeadsAsync(client, shop.TenantId));
	}

	private sealed record Shop(Guid TenantId, string Key, string HandoffEmail);

	private static async Task<Shop> SeedTenantAsync(HttpClient client)
	{
		var handoffEmail = $"help-{Guid.NewGuid():N}@shop.example.com";
		using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/tenants")
		{
			Content = JsonContent.Create(new { name = "Shop", handoffEmail, defaultLanguage = "English", allowedOrigins = new[] { AllowedOrigin } }),
		};
		create.Headers.Add(AdminOptions.HeaderName, AdminKey);
		var tenant = await (await client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>();
		var tenantId = tenant.GetProperty("id").GetGuid();

		using var form = new MultipartFormDataContent();
		var file = new ByteArrayContent("# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt."u8.ToArray());
		file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
		form.Add(file, "file", "policy.md");
		using var upload = new HttpRequestMessage(HttpMethod.Post, $"/v1/admin/tenants/{tenantId}/documents") { Content = form };
		upload.Headers.Add(AdminOptions.HeaderName, AdminKey);
		(await client.SendAsync(upload)).EnsureSuccessStatusCode();

		var deadline = DateTime.UtcNow + ReadyTimeout;
		while (DateTime.UtcNow < deadline)
		{
			using var list = new HttpRequestMessage(HttpMethod.Get, $"/v1/admin/tenants/{tenantId}/documents");
			list.Headers.Add(AdminOptions.HeaderName, AdminKey);
			var documents = await (await client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
			if (documents[0].GetProperty("status").GetString() == "Ready")
			{
				return new Shop(tenantId, tenant.GetProperty("publicKey").GetString()!, handoffEmail);
			}

			await Task.Delay(TimeSpan.FromMilliseconds(250));
		}

		throw new TimeoutException("The seeded document never became Ready.");
	}

	/// <summary>Asks and returns the conversation id from the final "done" event.</summary>
	private static async Task<Guid> AskAsync(HttpClient client, string key, string question, Guid? conversationId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/chat/{key}") { Content = JsonContent.Create(new { question, conversationId }) };
		request.Headers.Add("Origin", AllowedOrigin);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		var body = await response.Content.ReadAsStringAsync();
		var done = body.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
			.Select(block => block.Split('\n'))
			.Single(lines => lines.Contains("event: done"))
			.Single(line => line.StartsWith("data:", StringComparison.Ordinal))["data:".Length..];
		return JsonSerializer.Deserialize<JsonElement>(done, Json).GetProperty("conversationId").GetGuid();
	}

	private static async Task<HttpResponseMessage> PostHandoffAsync(HttpClient client, string key, object body, string origin = AllowedOrigin)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/handoff/{key}") { Content = JsonContent.Create(body) };
		request.Headers.Add("Origin", origin);
		return await client.SendAsync(request);
	}

	private static async Task<List<JsonElement>> ListLeadsAsync(HttpClient client, Guid tenantId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/admin/tenants/{tenantId}/leads");
		request.Headers.Add(AdminOptions.HeaderName, AdminKey);
		var leads = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
		return [.. leads.EnumerateArray()];
	}

	private static async Task<JsonElement> WaitForLeadStatusAsync(HttpClient client, Guid tenantId, string status)
	{
		var deadline = DateTime.UtcNow + MailTimeout;
		while (DateTime.UtcNow < deadline)
		{
			var lead = (await ListLeadsAsync(client, tenantId)).Single();
			if (lead.GetProperty("status").GetString() == status)
			{
				return lead;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(250));
		}

		throw new TimeoutException($"The lead never became {status}.");
	}
}
