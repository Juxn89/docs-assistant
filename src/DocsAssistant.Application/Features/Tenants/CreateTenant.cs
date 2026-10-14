using System.Net.Mail;
using System.Security.Cryptography;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Tenants;

namespace DocsAssistant.Application.Features.Tenants;

public sealed record CreateTenantRequest(
	string Name,
	string HandoffEmail,
	ContentLanguage DefaultLanguage,
	IReadOnlyList<string> AllowedOrigins);

public sealed record TenantResponse(
	Guid Id,
	string Name,
	string PublicKey,
	string HandoffEmail,
	ContentLanguage DefaultLanguage,
	IReadOnlyList<string> AllowedOrigins)
{
	public static TenantResponse From(Tenant tenant)
	{
		ArgumentNullException.ThrowIfNull(tenant);
		return new(tenant.Id.Value, tenant.Name, tenant.PublicKey, tenant.HandoffEmail, tenant.DefaultLanguage, tenant.AllowedOrigins);
	}
}

public sealed class CreateTenant(ITenantRepository tenants, TimeProvider clock)
{
	private const string PublicKeyPrefix = "pk_";

	/// <summary>18 random bytes → 24 base64url characters: unguessable, and short enough for a script tag.</summary>
	private const int PublicKeyRandomBytes = 18;

	public async Task<Result<TenantResponse>> HandleAsync(CreateTenantRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (string.IsNullOrWhiteSpace(request.Name))
		{
			return Error.Validation("tenant.name_required", "A tenant name is required.");
		}

		if (!MailAddress.TryCreate(request.HandoffEmail, out _))
		{
			return Error.Validation("tenant.invalid_email", "The handoff email is not a valid address.");
		}

		Tenant tenant;
		try
		{
			tenant = Tenant.Create(
				request.Name,
				NewPublicKey(),
				request.HandoffEmail,
				request.DefaultLanguage,
				request.AllowedOrigins ?? [],
				clock.GetUtcNow());
		}
		catch (DomainException exception)
		{
			return Error.Validation("tenant.invalid_origin", exception.Message);
		}

		tenants.Add(tenant);
		await tenants.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return TenantResponse.From(tenant);
	}

	private static string NewPublicKey() =>
		PublicKeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(PublicKeyRandomBytes))
			.Replace('+', '-')
			.Replace('/', '_');
}
