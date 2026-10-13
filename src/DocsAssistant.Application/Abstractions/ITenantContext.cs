using DocsAssistant.Domain.Common;

namespace DocsAssistant.Application.Abstractions;

/// <summary>
/// The tenant the current unit of work acts for. Set once per request (from the widget key or the
/// admin route) or per background job (from the document being processed).
/// Persistence filters every tenant-owned query by it and fails closed: with no tenant set, those
/// queries return nothing rather than everything.
/// </summary>
public interface ITenantContext
{
	TenantId? CurrentTenantId { get; }

	void Set(TenantId tenantId);
}

/// <summary>Scoped, mutable-once implementation. Re-binding to a different tenant is a bug.</summary>
public sealed class TenantContext : ITenantContext
{
	public TenantId? CurrentTenantId { get; private set; }

	public void Set(TenantId tenantId)
	{
		if (CurrentTenantId is { } current && current != tenantId)
		{
			throw new InvalidOperationException(
				$"This scope is already bound to tenant {current}; refusing to switch to {tenantId}.");
		}

		CurrentTenantId = tenantId;
	}
}
