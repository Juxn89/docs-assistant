using DocsAssistant.Domain.Common;

namespace DocsAssistant.Domain.Tenants;

/// <summary>
/// A customer of the assistant: their documents, their widget key, and where conversations are
/// handed off when the assistant cannot answer. Every query in the system is scoped to one tenant.
/// </summary>
public sealed class Tenant
{
	private readonly List<string> _allowedOrigins = [];

	private Tenant()
	{
		Name = string.Empty;
		PublicKey = string.Empty;
		HandoffEmail = string.Empty;
	}

	public TenantId Id { get; private set; }

	public string Name { get; private set; }

	/// <summary>Shipped inside the widget snippet. Identifies the tenant, grants nothing on its own.</summary>
	public string PublicKey { get; private set; }

	public string HandoffEmail { get; private set; }

	public ContentLanguage DefaultLanguage { get; private set; }

	public DateTimeOffset CreatedAt { get; private set; }

	/// <summary>Origins (scheme + host + port) where the widget may run. Empty means none.</summary>
	public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

	public static Tenant Create(
		string name,
		string publicKey,
		string handoffEmail,
		ContentLanguage defaultLanguage,
		IEnumerable<string> allowedOrigins,
		DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(handoffEmail);
		ArgumentNullException.ThrowIfNull(allowedOrigins);

		var tenant = new Tenant
		{
			Id = TenantId.New(),
			Name = name.Trim(),
			PublicKey = publicKey,
			HandoffEmail = handoffEmail.Trim(),
			DefaultLanguage = defaultLanguage,
			CreatedAt = now,
		};
		foreach (var origin in allowedOrigins)
		{
			tenant.AllowOrigin(origin);
		}

		return tenant;
	}

	public void AllowOrigin(string origin)
	{
		var normalized = NormalizeOrigin(origin)
			?? throw new DomainException($"'{origin}' is not a valid origin (expected scheme://host[:port]).");
		if (!_allowedOrigins.Contains(normalized, StringComparer.Ordinal))
		{
			_allowedOrigins.Add(normalized);
		}
	}

	public bool IsOriginAllowed(string? origin)
	{
		var normalized = NormalizeOrigin(origin);
		return normalized is not null && _allowedOrigins.Contains(normalized, StringComparer.Ordinal);
	}

	private static string? NormalizeOrigin(string? origin)
	{
		if (string.IsNullOrWhiteSpace(origin)
			|| !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
			|| uri.PathAndQuery is not ("/" or ""))
		{
			return null;
		}

		return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
	}
}
