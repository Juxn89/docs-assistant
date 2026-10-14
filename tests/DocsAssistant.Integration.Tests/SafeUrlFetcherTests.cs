using System.Net;
using System.Text;
using DocsAssistant.Infrastructure.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// SSRF defences against a real local HTTP server. With the default options the fetcher must refuse
/// the server outright (it is on loopback); the redirect and size tests opt into private networks to
/// reach it, which is exactly what deployed configuration never does.
/// </summary>
public sealed class SafeUrlFetcherTests : IAsyncLifetime
{
	private WebApplication _server = null!;
	private string _baseUrl = null!;

	public async Task InitializeAsync()
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		_server = builder.Build();
		_server.MapGet("/page", () => Results.Content("<html><title>Hi</title><p>Hello</p></html>", "text/html"));
		_server.MapGet("/redirect/{hops:int}", (int hops) =>
			Results.Redirect(hops <= 1 ? "/page" : $"/redirect/{hops - 1}"));
		_server.MapGet("/big", () => Results.Text(new string('x', 2048), "text/plain"));
		_server.MapGet("/to-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
		await _server.StartAsync();
		_baseUrl = _server.Urls.First();
	}

	public async Task DisposeAsync() => await _server.DisposeAsync();

	private static void TrustLocalServer(UrlFetchOptions options) => options.TrustedAddresses.Add(IPAddress.Loopback.ToString());

	private SafeUrlFetcher Fetcher(Action<UrlFetchOptions>? configure = null)
	{
		var options = new UrlFetchOptions { Timeout = TimeSpan.FromSeconds(5) };

		// The test server listens on a random port. Allowing it isolates every test from the port
		// check, so each one proves the defence it is named after and not a different one.
		options.AllowedPorts.Add(new Uri(_baseUrl).Port);
		configure?.Invoke(options);
		return new SafeUrlFetcher(Options.Create(options));
	}

	[Theory]
	[InlineData("http://127.0.0.1/")]
	[InlineData("http://169.254.169.254/latest/meta-data/")]
	[InlineData("http://10.0.0.5/")]
	[InlineData("http://192.168.1.1/")]
	[InlineData("http://[::1]/")]
	public async Task PrivateLiterals_AreBlockedBeforeConnecting(string url)
	{
		using var fetcher = Fetcher();

		var result = await fetcher.FetchAsync(new Uri(url), CancellationToken.None);

		Assert.Equal("url.blocked", result.Error?.Code);
	}

	[Fact]
	public async Task HostnameResolvingToLoopback_IsBlockedAtConnectTime()
	{
		using var fetcher = Fetcher();
		var port = new Uri(_baseUrl).Port;

		// "localhost" is not an IP literal, so only the connect-time check can stop it.
		var result = await fetcher.FetchAsync(new Uri($"http://localhost:{port}/page"), CancellationToken.None);

		Assert.Equal("url.blocked", result.Error?.Code);
	}

	[Theory]
	[InlineData("ftp://example.com/file")]
	[InlineData("http://user:secret@example.com/")]
	[InlineData("http://example.com:5432/")]
	public async Task NonWebShapes_AreBlocked(string url)
	{
		using var fetcher = Fetcher();

		var result = await fetcher.FetchAsync(new Uri(url), CancellationToken.None);

		Assert.Equal("url.blocked", result.Error?.Code);
	}

	[Fact]
	public async Task Redirects_AreFollowedAndTheFinalUrlIsReported()
	{
		using var fetcher = Fetcher(TrustLocalServer);

		var result = await fetcher.FetchAsync(new Uri($"{_baseUrl}/redirect/2"), CancellationToken.None);

		Assert.True(result.IsSuccess, result.Error?.Message);
		Assert.EndsWith("/page", result.Value.FinalUri.AbsolutePath, StringComparison.Ordinal);
		Assert.Equal("text/html", result.Value.MediaType);
		Assert.Contains("Hello", Encoding.UTF8.GetString(result.Value.Content.Span), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TooManyRedirects_Fail()
	{
		using var fetcher = Fetcher(o => { TrustLocalServer(o); o.MaxRedirects = 1; });

		var result = await fetcher.FetchAsync(new Uri($"{_baseUrl}/redirect/3"), CancellationToken.None);

		Assert.Equal("url.too_many_redirects", result.Error?.Code);
	}

	[Fact]
	public async Task RedirectToCloudMetadata_IsBlocked()
	{
		// The first hop is allowed only because 127.0.0.1 is trusted for the test server; the redirect
		// target is checked on its own and refused — trusting one address trusts nothing else.
		using var fetcher = Fetcher(TrustLocalServer);

		var result = await fetcher.FetchAsync(new Uri($"{_baseUrl}/to-metadata"), CancellationToken.None);

		Assert.Equal("url.blocked", result.Error?.Code);
	}

	[Fact]
	public async Task BodiesOverTheCap_AreRejectedWhileStreaming()
	{
		using var fetcher = Fetcher(o => { TrustLocalServer(o); o.MaxBytes = 1024; });

		var result = await fetcher.FetchAsync(new Uri($"{_baseUrl}/big"), CancellationToken.None);

		Assert.Equal("url.too_large", result.Error?.Code);
	}

	[Theory]
	[InlineData("8.8.8.8", true)]
	[InlineData("1.1.1.1", true)]
	[InlineData("2606:4700:4700::1111", true)]
	[InlineData("127.0.0.1", false)]
	[InlineData("0.0.0.0", false)]
	[InlineData("10.1.2.3", false)]
	[InlineData("172.16.0.1", false)]
	[InlineData("172.31.255.255", false)]
	[InlineData("172.32.0.1", true)]
	[InlineData("192.168.0.1", false)]
	[InlineData("100.64.0.1", false)]
	[InlineData("169.254.169.254", false)]
	[InlineData("224.0.0.1", false)]
	[InlineData("::1", false)]
	[InlineData("fe80::1", false)]
	[InlineData("fd00::1", false)]
	[InlineData("::ffff:127.0.0.1", false)]
	[InlineData("::ffff:10.0.0.1", false)]
	public void IsPublic_ClassifiesAddresses(string address, bool expected) =>
		Assert.Equal(expected, SafeUrlFetcher.IsPublic(IPAddress.Parse(address)));
}
