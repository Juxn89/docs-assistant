using DocsAssistant.Application.Abstractions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace DocsAssistant.Api.Cors;

/// <summary>
/// CORS per assistant: the widget on a customer's site may call the API only from the origins that
/// customer registered. A static policy cannot express that (every tenant has its own list), so the
/// policy is built from the public key in the path. Admin routes get no policy at all: the admin API
/// is not meant to be called from a browser on another site.
///
/// The origins are cached briefly: preflights are not rate limited per assistant, and a page that
/// asks several questions would otherwise read the tenant on every one. An origin added or removed
/// takes effect within <see cref="CacheDuration"/>.
/// </summary>
internal sealed class WidgetCorsPolicyProvider : ICorsPolicyProvider, IDisposable
{
	public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

	/// <summary>Bounds memory when someone sends preflights for thousands of made-up keys.</summary>
	private const int MaxCachedKeys = 10_000;

	private static readonly string[] WidgetPrefixes = ["/v1/chat/", "/v1/handoff/"];

	private readonly MemoryCache _origins = new(new MemoryCacheOptions { SizeLimit = MaxCachedKeys });

	public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (PublicKeyOf(context.Request.Path) is not { } key)
		{
			return null;
		}

		var origins = await _origins.GetOrCreateAsync(key, async entry =>
		{
			entry.AbsoluteExpirationRelativeToNow = CacheDuration;
			entry.Size = 1;
			var tenant = await context.RequestServices.GetRequiredService<ITenantRepository>()
				.GetByPublicKeyAsync(key, context.RequestAborted);
			return tenant?.AllowedOrigins.ToArray() ?? [];
		});

		if (origins is null or { Length: 0 })
		{
			return null;
		}

		return new CorsPolicyBuilder()
			.WithOrigins(origins)
			.WithMethods(HttpMethods.Post)
			.WithHeaders("content-type")
			// The widget reads it to tell the visitor when to try again.
			.WithExposedHeaders("retry-after")
			.SetPreflightMaxAge(TimeSpan.FromMinutes(10))
			.Build();
	}

	public void Dispose() => _origins.Dispose();

	/// <summary>The segment after /v1/chat/ or /v1/handoff/, or null for any other path.</summary>
	internal static string? PublicKeyOf(PathString path)
	{
		var value = path.Value ?? string.Empty;
		foreach (var prefix in WidgetPrefixes)
		{
			if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				var key = value[prefix.Length..].Split('/')[0];
				return key.Length == 0 ? null : key;
			}
		}

		return null;
	}
}
