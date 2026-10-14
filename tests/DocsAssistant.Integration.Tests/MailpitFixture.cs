using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// A real SMTP server that keeps what it receives and exposes it over HTTP. Same image and tag as the
/// AppHost's Mailpit resource (CommunityToolkit.Aspire.Hosting.MailPit 13.6.0).
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
	public const string Image = "axllent/mailpit:v1.31.2";
	private const int SmtpPort = 1025;
	private const int HttpPort = 8025;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly IContainer _container = new ContainerBuilder(Image)
		.WithPortBinding(SmtpPort, assignRandomHostPort: true)
		.WithPortBinding(HttpPort, assignRandomHostPort: true)
		.WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath("/livez")))
		.Build();

	private HttpClient? _api;

	public string SmtpHost => _container.Hostname;

	public int SmtpPortOnHost => _container.GetMappedPublicPort(SmtpPort);

	public async Task InitializeAsync()
	{
		await _container.StartAsync();
		_api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}") };
	}

	public async Task DisposeAsync()
	{
		_api?.Dispose();
		await _container.DisposeAsync();
	}

	/// <summary>Waits for the first email sent to <paramref name="to"/> and returns it in full.</summary>
	public async Task<JsonElement> WaitForEmailToAsync(string to, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			var list = await _api!.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString($"to:\"{to}\"")}", Json);
			var messages = list.GetProperty("messages");
			if (messages.GetArrayLength() > 0)
			{
				return await _api!.GetFromJsonAsync<JsonElement>($"/api/v1/message/{messages[0].GetProperty("ID").GetString()}", Json);
			}

			await Task.Delay(TimeSpan.FromMilliseconds(250));
		}

		throw new TimeoutException($"No email to {to} arrived within {timeout}.");
	}

	public async Task<int> CountEmailsToAsync(string to)
	{
		var list = await _api!.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString($"to:\"{to}\"")}", Json);
		return list.GetProperty("messages").GetArrayLength();
	}
}
