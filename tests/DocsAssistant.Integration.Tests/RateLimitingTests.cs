using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocsAssistant.Api.Admin;
using DocsAssistant.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// Limits are set tiny here so the behaviour is observable in a few requests. Unknown assistant keys
/// are used on purpose: the limiter runs before the endpoint, so a 404 still consumes a permit — which
/// is the point, or scanning for keys would be free.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class RateLimitingTests(PostgresFixture postgres) : IDisposable
{
	private const string AdminKey = "test-admin-key-0123456789abcdef-0123456789";
	private readonly List<WebApplicationFactory<Program>> _factories = [];

	public void Dispose()
	{
		foreach (var factory in _factories)
		{
			factory.Dispose();
		}
	}

	private HttpClient Client(Dictionary<string, string> limits)
	{
		var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", postgres.ConnectionString);
			builder.UseSetting($"{AdminOptions.SectionName}:ApiKey", AdminKey);
			foreach (var (key, value) in limits)
			{
				builder.UseSetting($"RateLimiting:{key}", value);
			}
		});
		_factories.Add(factory);
		return factory.CreateClient();
	}

	private static Task<HttpResponseMessage> AskAsync(HttpClient client, string key, string? forwardedFor = null)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/chat/{key}") { Content = JsonContent.Create(new { question = "hi" }) };
		if (forwardedFor is not null)
		{
			request.Headers.Add("X-Forwarded-For", forwardedFor);
		}

		return client.SendAsync(request);
	}

	[Fact]
	public async Task Chat_OverThePerMinuteLimit_Gets429WithRetryAfterAndACode()
	{
		using var client = Client(new() { ["ChatPerMinutePerIp"] = "3" });

		for (var i = 0; i < 3; i++)
		{
			Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(client, "pk_unknown")).StatusCode);
		}

		var limited = await AskAsync(client, "pk_unknown");

		Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
		Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After must tell the client when to come back.");
		var problem = await limited.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
	}

	[Fact]
	public async Task SpoofedForwardedFor_IsIgnoredWhenNotTrusted()
	{
		using var client = Client(new() { ["ChatPerMinutePerIp"] = "2", ["TrustForwardedHeaders"] = "false" });

		// Claiming a new address on every request must not buy a fresh quota.
		await AskAsync(client, "pk_unknown", "203.0.113.1");
		await AskAsync(client, "pk_unknown", "203.0.113.2");
		var third = await AskAsync(client, "pk_unknown", "203.0.113.3");

		Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
	}

	[Fact]
	public async Task TrustedForwardedFor_SeparatesClientsBehindTheProxy()
	{
		using var client = Client(new() { ["ChatPerMinutePerIp"] = "1", ["TrustForwardedHeaders"] = "true" });

		var first = await AskAsync(client, "pk_unknown", "198.51.100.10");
		var sameClient = await AskAsync(client, "pk_unknown", "198.51.100.10");
		var otherClient = await AskAsync(client, "pk_unknown", "198.51.100.20");

		Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, otherClient.StatusCode);
	}

	[Fact]
	public async Task Ipv6Clients_AreLimitedPerSlash64()
	{
		using var client = Client(new() { ["ChatPerMinutePerIp"] = "1", ["TrustForwardedHeaders"] = "true" });

		await AskAsync(client, "pk_unknown", "2001:db8:1:2::1");
		// Same /64, different interface id: still the same client.
		var rotated = await AskAsync(client, "pk_unknown", "2001:db8:1:2:ffff::9");

		Assert.Equal(HttpStatusCode.TooManyRequests, rotated.StatusCode);
	}

	[Fact]
	public async Task PerTenantDailyCap_HoldsAcrossClients_AndOnlyForThatTenant()
	{
		using var client = Client(new() { ["ChatPerDayPerTenant"] = "2", ["TrustForwardedHeaders"] = "true" });

		await AskAsync(client, "pk_busy", "198.51.100.1");
		await AskAsync(client, "pk_busy", "198.51.100.2");
		var thirdClient = await AskAsync(client, "pk_busy", "198.51.100.3");
		var otherTenant = await AskAsync(client, "pk_quiet", "198.51.100.3");

		Assert.Equal(HttpStatusCode.TooManyRequests, thirdClient.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
	}

	[Fact]
	public async Task AdminKeyGuessing_IsSlowedDown()
	{
		using var client = Client(new() { ["AdminPerMinutePerIp"] = "2" });
		client.DefaultRequestHeaders.Add(AdminOptions.HeaderName, "guess");

		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/v1/admin/tenants/{Guid.NewGuid()}/documents")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/v1/admin/tenants/{Guid.NewGuid()}/documents")).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/v1/admin/tenants/{Guid.NewGuid()}/documents")).StatusCode);
	}

	[Fact]
	public async Task HealthChecks_AreNeverLimited()
	{
		using var client = Client(new() { ["ChatPerMinutePerIp"] = "1", ["AdminPerMinutePerIp"] = "1" });

		for (var i = 0; i < 5; i++)
		{
			Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/health")).StatusCode);
		}
	}
}
