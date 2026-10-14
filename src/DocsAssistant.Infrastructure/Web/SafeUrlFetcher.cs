using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Infrastructure.Web;

public sealed class UrlFetchOptions
{
	public const string SectionName = "UrlFetch";

	[Range(1, 20 * 1024 * 1024)]
	public int MaxBytes { get; set; } = 5 * 1024 * 1024;

	[Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

	[Range(0, 10)]
	public int MaxRedirects { get; set; } = 3;

	/// <summary>Web ports only, so a URL cannot be used to probe databases or admin consoles.</summary>
	[MinLength(1)]
	public IList<int> AllowedPorts { get; } = [80, 443, 8080, 8443];

	/// <summary>
	/// Exact non-public addresses that may still be fetched. Tests only (a local test server on
	/// 127.0.0.1); empty in every deployed configuration. Exact addresses, not ranges, so allowing a test
	/// server never silently allows the cloud metadata endpoint too.
	/// </summary>
	public IList<string> TrustedAddresses { get; } = [];
}

/// <summary>
/// Server-side request forgery is the risk of letting a tenant say "index this URL": without care,
/// "http://169.254.169.254/" reads the cloud metadata service and "http://localhost:5432" probes the
/// database. Defences, in order:
/// <list type="number">
/// <item>Only http/https, no credentials in the URL, default or standard ports only.</item>
/// <item>The destination IP is checked <b>at connect time</b>, inside the socket callback, against
/// loopback, private, link-local, CGNAT, multicast and reserved ranges. Checking the hostname before
/// the request is not enough: DNS can answer differently a second later (rebinding).</item>
/// <item>Redirects are followed by hand, at most <see cref="UrlFetchOptions.MaxRedirects"/>, and each
/// hop goes through the same checks.</item>
/// <item>Size and time are capped while streaming, not after downloading.</item>
/// </list>
/// </summary>
internal sealed class SafeUrlFetcher : IUrlFetcher, IDisposable
{
	private const string UserAgent = "docs-assistant/0.1 (+https://github.com/Juxn89/docs-assistant)";
	private const int CopyBufferBytes = 81920;

	private readonly UrlFetchOptions _options;
	private readonly HttpClient _client;

	public SafeUrlFetcher(IOptions<UrlFetchOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options.Value;

		var handler = new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = _options.Timeout,
			ConnectCallback = ConnectToPublicAddressAsync,
		};
		_client = new HttpClient(handler) { Timeout = _options.Timeout };
		_client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		_client.DefaultRequestHeaders.Accept.ParseAdd("text/html, text/plain, text/markdown, application/pdf;q=0.9, */*;q=0.1");
	}

	public async Task<Result<FetchedResource>> FetchAsync(Uri url, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(url);

		var current = url;
		for (var hop = 0; hop <= _options.MaxRedirects; hop++)
		{
			if (CheckUrlShape(current) is { } shapeError)
			{
				return shapeError;
			}

			HttpResponseMessage response;
			try
			{
				response = await _client
					.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (HttpRequestException exception) when (exception.InnerException is BlockedDestinationException blocked)
			{
				return Error.Validation("url.blocked", blocked.Message);
			}
			catch (HttpRequestException exception)
			{
				return Error.Upstream("url.unreachable", $"Could not reach {current.Host}: {exception.Message}");
			}
			catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return Error.Upstream("url.timeout", $"{current.Host} did not answer within {_options.Timeout.TotalSeconds:N0}s.");
			}

			using (response)
			{
				if (IsRedirect(response.StatusCode))
				{
					if (response.Headers.Location is not { } location)
					{
						return Error.Upstream("url.bad_redirect", "Redirect without a Location header.");
					}

					current = location.IsAbsoluteUri ? location : new Uri(current, location);
					continue;
				}

				if (!response.IsSuccessStatusCode)
				{
					return Error.Upstream("url.http_error", $"{current} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
				}

				if (response.Content.Headers.ContentLength > _options.MaxBytes)
				{
					return TooLarge();
				}

				var body = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
				if (body is null)
				{
					return TooLarge();
				}

				return new FetchedResource(
					current,
					response.Content.Headers.ContentType?.MediaType,
					body,
					FileNameOf(response.Content.Headers.ContentDisposition, current));
			}
		}

		return Error.Upstream("url.too_many_redirects", $"More than {_options.MaxRedirects} redirects.");
	}

	public void Dispose() => _client.Dispose();

	internal static bool IsPublic(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}

		if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
		{
			return false;
		}

		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
				|| address.IsIPv6UniqueLocal || address.IsIPv6Teredo || address.Equals(IPAddress.IPv6None));
		}

		var b = address.GetAddressBytes();
		return b[0] switch
		{
			0 or 10 or 127 => false,                       // "this network", private, loopback
			100 when b[1] is >= 64 and <= 127 => false,    // CGNAT 100.64.0.0/10
			169 when b[1] == 254 => false,                 // link-local, incl. cloud metadata 169.254.169.254
			172 when b[1] is >= 16 and <= 31 => false,     // private 172.16.0.0/12
			192 when b[1] == 168 => false,                 // private 192.168.0.0/16
			192 when b[1] == 0 && b[2] is 0 or 2 => false, // IETF protocol assignments, TEST-NET-1
			198 when b[1] is 18 or 19 => false,            // benchmarking 198.18.0.0/15
			>= 224 => false,                               // multicast and reserved
			_ => true,
		};
	}

	private async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
	{
		var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
		var allowed = addresses.Where(IsAllowed).ToArray();
		if (allowed.Length == 0)
		{
			throw new BlockedDestinationException(
				$"{context.DnsEndPoint.Host} resolves only to private or reserved addresses, which cannot be fetched.");
		}

		var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try
		{
			// Connect to exactly the address that was checked, never re-resolving the name.
			await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private bool IsAllowed(IPAddress address) =>
		IsPublic(address) || _options.TrustedAddresses.Contains(address.ToString());

	private Error? CheckUrlShape(Uri url)
	{
		if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
		{
			return Error.Validation("url.blocked", "Only http and https URLs can be fetched.");
		}

		if (!string.IsNullOrEmpty(url.UserInfo))
		{
			return Error.Validation("url.blocked", "URLs with embedded credentials are not allowed.");
		}

		if (!_options.AllowedPorts.Contains(url.Port))
		{
			return Error.Validation("url.blocked", $"Port {url.Port} is not allowed.");
		}

		if (IPAddress.TryParse(url.DnsSafeHost, out var literal) && !IsAllowed(literal))
		{
			return Error.Validation("url.blocked", "Private, loopback and reserved addresses cannot be fetched.");
		}

		return null;
	}

	private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
	{
		await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		var chunk = new byte[CopyBufferBytes];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > _options.MaxBytes)
			{
				return null;
			}

			buffer.Write(chunk, 0, read);
		}

		return buffer.ToArray();
	}

	private Error TooLarge() =>
		Error.Validation("url.too_large", $"The resource is larger than {_options.MaxBytes:N0} bytes.");

	private static bool IsRedirect(HttpStatusCode status) =>
		status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
			or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

	private static string? FileNameOf(ContentDispositionHeaderValue? disposition, Uri url)
	{
		var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
		if (!string.IsNullOrWhiteSpace(name))
		{
			return name;
		}

		var last = url.Segments.LastOrDefault()?.Trim('/');
		return string.IsNullOrEmpty(last) ? null : Uri.UnescapeDataString(last);
	}

	private sealed class BlockedDestinationException(string message) : IOException(message);
}
