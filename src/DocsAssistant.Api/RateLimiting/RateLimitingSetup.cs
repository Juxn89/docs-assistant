using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Api.RateLimiting;

/// <summary>Bound from "RateLimiting". Defaults are for a public demo on a single instance.</summary>
public sealed class RateLimitingOptions
{
	public const string SectionName = "RateLimiting";

	/// <summary>Burst control: a person typing does not ask more than this in a minute.</summary>
	[Range(1, 10_000)]
	public int ChatPerMinutePerIp { get; set; } = 10;

	/// <summary>Daily cap per visitor, so one address cannot exhaust the demo for everyone else.</summary>
	[Range(1, 1_000_000)]
	public int ChatPerDayPerIp { get; set; } = 60;

	/// <summary>Daily cap per assistant (tenant), so heavy use of one site cannot starve the others.</summary>
	[Range(1, 10_000_000)]
	public int ChatPerDayPerTenant { get; set; } = 2_000;

	/// <summary>
	/// Each handoff sends an email to a real person: low enough that the form cannot be used to flood
	/// an operator's inbox, high enough for a visitor who rephrases.
	/// </summary>
	[Range(1, 10_000)]
	public int HandoffPerHourPerIp { get; set; } = 5;

	/// <summary>
	/// CORS preflights (OPTIONS) are not counted against the chat quota — each question would cost two
	/// permits — but they read the tenant, so they get a separate, generous cap.
	/// </summary>
	[Range(1, 10_000)]
	public int PreflightPerMinutePerIp { get; set; } = 60;

	/// <summary>Slows down guessing the admin key; legitimate admin use is far below this.</summary>
	[Range(1, 10_000)]
	public int AdminPerMinutePerIp { get; set; } = 60;

	/// <summary>
	/// Trust X-Forwarded-For for the client address. Only behind a proxy that always sets it (Azure
	/// Container Apps ingress, a load balancer) and only when the app is unreachable any other way —
	/// otherwise anyone can claim any address and get a fresh quota on every request.
	/// </summary>
	public bool TrustForwardedHeaders { get; set; }
}

internal static class RateLimitingSetup
{
	public const string RejectedCode = "rate_limited";

	private const string ChatPrefix = "/v1/chat/";
	private const string AdminPrefix = "/v1/admin";
	private const string HandoffPrefix = "/v1/handoff/";
	private const string UnknownClient = "unknown";

	private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
	private static readonly TimeSpan Day = TimeSpan.FromDays(1);

	public static IServiceCollection AddDocsAssistantRateLimiting(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<RateLimitingOptions>()
			.Bind(configuration.GetSection(RateLimitingOptions.SectionName))
			.ValidateDataAnnotations()
			.ValidateOnStart();

		services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<RateLimitingOptions>>((forwarded, limits) =>
		{
			if (!limits.Value.TrustForwardedHeaders)
			{
				return;
			}

			forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
			// The ingress is the only hop: take exactly one address from the header, the one it appended.
			forwarded.ForwardLimit = 1;
			forwarded.KnownIPNetworks.Clear();
			forwarded.KnownProxies.Clear();
		});

		services.AddRateLimiter(_ => { });
		services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>>((limiter, settings) =>
		{
			var limits = settings.Value;
			limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
				PerClient(ChatPrefix, limits.ChatPerMinutePerIp, Minute),
				PerClient(ChatPrefix, limits.ChatPerDayPerIp, Day),
				PerTenant(limits.ChatPerDayPerTenant, Day),
				PerClient(HandoffPrefix, limits.HandoffPerHourPerIp, Hour),
				PerClient(AdminPrefix, limits.AdminPerMinutePerIp, Minute),
				Preflights(limits.PreflightPerMinutePerIp, Minute));
			limiter.OnRejected = RejectAsync;
		});

		return services;
	}

	/// <summary>
	/// First in the pipeline when enabled: CORS and the limits must see the real client address.
	/// </summary>
	public static WebApplication UseDocsAssistantForwardedHeaders(this WebApplication app)
	{
		if (app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value.TrustForwardedHeaders)
		{
			app.UseForwardedHeaders();
		}

		return app;
	}

	/// <summary>
	/// After CORS, so a 429 still carries the CORS headers: without them the browser hides the status
	/// and the widget cannot tell "slow down" from "the server is down".
	/// </summary>
	public static WebApplication UseDocsAssistantRateLimiting(this WebApplication app)
	{
		app.UseRateLimiter();
		return app;
	}

	/// <summary>A fixed window per client address, applied only to non-preflight requests under <paramref name="prefix"/>.</summary>
	private static PartitionedRateLimiter<HttpContext> PerClient(string prefix, int permits, TimeSpan window) =>
		PartitionedRateLimiter.Create<HttpContext, string>(context =>
			!HttpMethods.IsOptions(context.Request.Method)
				&& context.Request.Path.StartsWithSegments(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
				? RateLimitPartition.GetFixedWindowLimiter($"{prefix}|{ClientOf(context)}", _ => Window(permits, window))
				: RateLimitPartition.GetNoLimiter(string.Empty));

	/// <summary>A fixed window per assistant public key (the route segment after /v1/chat/).</summary>
	private static PartitionedRateLimiter<HttpContext> PerTenant(int permits, TimeSpan window) =>
		PartitionedRateLimiter.Create<HttpContext, string>(context =>
		{
			var path = context.Request.Path.Value ?? string.Empty;
			return !HttpMethods.IsOptions(context.Request.Method)
				&& path.StartsWith(ChatPrefix, StringComparison.OrdinalIgnoreCase) && path.Length > ChatPrefix.Length
				? RateLimitPartition.GetFixedWindowLimiter($"tenant|{path[ChatPrefix.Length..]}", _ => Window(permits, window))
				: RateLimitPartition.GetNoLimiter(string.Empty);
		});

	/// <summary>Preflights to the widget endpoints, per client address.</summary>
	private static PartitionedRateLimiter<HttpContext> Preflights(int permits, TimeSpan window) =>
		PartitionedRateLimiter.Create<HttpContext, string>(context =>
			HttpMethods.IsOptions(context.Request.Method)
				&& (context.Request.Path.StartsWithSegments(ChatPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
					|| context.Request.Path.StartsWithSegments(HandoffPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
				? RateLimitPartition.GetFixedWindowLimiter($"preflight|{ClientOf(context)}", _ => Window(permits, window))
				: RateLimitPartition.GetNoLimiter(string.Empty));

	private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
	{
		PermitLimit = permits,
		Window = window,
		QueueLimit = 0,
		AutoReplenishment = true,
	};

	/// <summary>
	/// The connection's address — after UseForwardedHeaders, the client's address as the trusted proxy
	/// saw it. IPv6 clients are grouped by /64: one household or phone gets a whole /64, so per-address
	/// limits on IPv6 would be trivially bypassed by rotating the low bits.
	/// </summary>
	private static string ClientOf(HttpContext context)
	{
		var address = context.Connection.RemoteIpAddress;
		if (address is null)
		{
			return UnknownClient;
		}

		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}

		if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
		{
			const int PrefixBytes = 8;
			var bytes = address.GetAddressBytes();
			Array.Clear(bytes, PrefixBytes, bytes.Length - PrefixBytes);
			address = new IPAddress(bytes);
		}

		return address.ToString();
	}

	private static async ValueTask RejectAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
	{
		var response = rejected.HttpContext.Response;
		response.StatusCode = StatusCodes.Status429TooManyRequests;
		if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
		{
			response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
		}

		await Results.Problem(
			statusCode: StatusCodes.Status429TooManyRequests,
			title: "Too many requests. Please wait a moment and try again.",
			type: "https://github.com/Juxn89/docs-assistant/blob/main/docs/errors.md#rate_limited",
			extensions: new Dictionary<string, object?> { ["code"] = RejectedCode })
			.ExecuteAsync(rejected.HttpContext);
	}
}
