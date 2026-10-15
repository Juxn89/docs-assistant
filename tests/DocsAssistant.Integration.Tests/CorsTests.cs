using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocsAssistant.Api.Admin;
using DocsAssistant.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The widget runs on the customer's site and calls the API cross-origin. The browser only lets it
/// read the answer when the API names that site in Access-Control-Allow-Origin — and only for the
/// sites the customer registered.
/// </summary>
[Collection(IngestionCollection.Name)]
public sealed class CorsTests(PostgresFixture postgres) : IDisposable
{
	private const string AdminKey = "test-admin-key-0123456789abcdef-0123456789";
	private const string AllowedOrigin = "https://shop.example.com";
	private const string OtherOrigin = "https://evil.example.net";
	private readonly List<WebApplicationFactory<Program>> _factories = [];

	public void Dispose()
	{
		foreach (var factory in _factories)
		{
			factory.Dispose();
		}
	}

	private HttpClient Client(int chatPerMinute = 10_000)
	{
		var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", postgres.ConnectionString);
			builder.UseSetting($"{AdminOptions.SectionName}:ApiKey", AdminKey);
			builder.UseSetting("RateLimiting:ChatPerMinutePerIp", chatPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
		});
		_factories.Add(factory);
		return factory.CreateClient();
	}

	private static async Task<string> SeedTenantAsync(HttpClient client)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/tenants")
		{
			Content = JsonContent.Create(new { name = "Shop", handoffEmail = "help@shop.example.com", defaultLanguage = "English", allowedOrigins = new[] { AllowedOrigin } }),
		};
		request.Headers.Add(AdminOptions.HeaderName, AdminKey);
		var tenant = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
		return tenant.GetProperty("publicKey").GetString()!;
	}

	private static Task<HttpResponseMessage> PreflightAsync(HttpClient client, string path, string origin)
	{
		var request = new HttpRequestMessage(HttpMethod.Options, path);
		request.Headers.Add("Origin", origin);
		request.Headers.Add("Access-Control-Request-Method", "POST");
		request.Headers.Add("Access-Control-Request-Headers", "content-type");
		return client.SendAsync(request);
	}

	private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string origin)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("Origin", origin);
		return client.SendAsync(request);
	}

	private static string? AllowedOriginOf(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

	[Theory]
	[InlineData("/v1/chat/{0}")]
	[InlineData("/v1/handoff/{0}")]
	public async Task Preflight_FromARegisteredSite_IsAllowed(string pathFormat)
	{
		using var client = Client();
		var key = await SeedTenantAsync(client);

		var response = await PreflightAsync(client, string.Format(System.Globalization.CultureInfo.InvariantCulture, pathFormat, key), AllowedOrigin);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
		Assert.Equal(AllowedOrigin, AllowedOriginOf(response));
		Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Preflight_FromAnotherSite_UnknownKey_OrAdminRoute_GetsNoAllowOrigin()
	{
		using var client = Client();
		var key = await SeedTenantAsync(client);

		Assert.Null(AllowedOriginOf(await PreflightAsync(client, $"/v1/chat/{key}", OtherOrigin)));
		Assert.Null(AllowedOriginOf(await PreflightAsync(client, "/v1/chat/pk_unknown", AllowedOrigin)));
		Assert.Null(AllowedOriginOf(await PreflightAsync(client, "/v1/admin/tenants", AllowedOrigin)));
	}

	[Fact]
	public async Task Responses_ToTheRegisteredSite_CarryAllowOrigin()
	{
		using var client = Client();
		var key = await SeedTenantAsync(client);

		var handoff = await PostAsync(client, $"/v1/handoff/{key}", new { email = "not-an-email", message = "hi" }, AllowedOrigin);

		Assert.Equal(HttpStatusCode.BadRequest, handoff.StatusCode);
		Assert.Equal(AllowedOrigin, AllowedOriginOf(handoff));
	}

	[Fact]
	public async Task RateLimitedResponse_StillCarriesAllowOrigin_SoTheWidgetCanReadIt()
	{
		using var client = Client(chatPerMinute: 1);
		var key = await SeedTenantAsync(client);
		await PostAsync(client, $"/v1/chat/{key}", new { question = "" }, AllowedOrigin);

		var limited = await PostAsync(client, $"/v1/chat/{key}", new { question = "" }, AllowedOrigin);

		Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
		Assert.Equal(AllowedOrigin, AllowedOriginOf(limited));
		Assert.Contains("retry-after", limited.Headers.GetValues("Access-Control-Expose-Headers").Single(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Preflights_DoNotSpendTheChatQuota()
	{
		using var client = Client(chatPerMinute: 2);
		var key = await SeedTenantAsync(client);

		for (var i = 0; i < 5; i++)
		{
			Assert.Equal(HttpStatusCode.NoContent, (await PreflightAsync(client, $"/v1/chat/{key}", AllowedOrigin)).StatusCode);
		}

		var first = await PostAsync(client, $"/v1/chat/{key}", new { question = "" }, AllowedOrigin);
		var second = await PostAsync(client, $"/v1/chat/{key}", new { question = "" }, AllowedOrigin);

		Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
	}
}
