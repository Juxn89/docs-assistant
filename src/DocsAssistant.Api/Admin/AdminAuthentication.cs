using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Api.Admin;

public sealed class AdminOptions
{
	public const string SectionName = "Admin";

	public const string HeaderName = "X-Admin-Key";

	/// <summary>
	/// Shared secret for the admin API. In production: OIDC with per-user identities. For a demo with a
	/// single operator a long random key in a secret store is proportionate — and it is never compared
	/// with ==, see <see cref="AdminKeyFilter"/>.
	/// </summary>
	[Required, MinLength(32)]
	public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Constant-time comparison: response timing must not reveal how much of a guess was right.</summary>
internal sealed class AdminKeyFilter(IOptions<AdminOptions> options) : IEndpointFilter
{
	private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);

	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(next);

		var provided = context.HttpContext.Request.Headers[AdminOptions.HeaderName].ToString();
		var providedBytes = Encoding.UTF8.GetBytes(provided);
		var valid = providedBytes.Length == _expected.Length && CryptographicOperations.FixedTimeEquals(providedBytes, _expected);

		return valid
			? next(context)
			: ValueTask.FromResult<object?>(Results.Problem(
				statusCode: StatusCodes.Status401Unauthorized,
				title: "Missing or invalid admin key",
				detail: $"Send the admin key in the {AdminOptions.HeaderName} header."));
	}
}
