using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Handoff;

namespace DocsAssistant.Application.Features.Handoff;

/// <summary>What the operator sees of a lead, including whether the email reached them.</summary>
public sealed record LeadResponse(
	Guid Id,
	Guid? ConversationId,
	string Email,
	string? Name,
	string Message,
	ContentLanguage Language,
	LeadStatus Status,
	int Attempts,
	string? LastError,
	DateTimeOffset CreatedAt,
	DateTimeOffset? NotifiedAt)
{
	public static LeadResponse From(Lead lead)
	{
		ArgumentNullException.ThrowIfNull(lead);
		return new(
			lead.Id.Value,
			lead.ConversationId?.Value,
			lead.VisitorEmail,
			lead.VisitorName,
			lead.Message,
			lead.Language,
			lead.Status,
			lead.Attempts,
			lead.LastError,
			lead.CreatedAt,
			lead.NotifiedAt);
	}
}

/// <summary>
/// Newest first. Listed even when the email failed: the lead is the record, the email only a
/// notification of it.
/// </summary>
public sealed class ListLeads(ITenantContext tenantContext, ITenantRepository tenants, ILeadRepository leads)
{
	public const int MaxResults = 200;

	public async Task<Result<IReadOnlyList<LeadResponse>>> HandleAsync(Guid tenantId, CancellationToken cancellationToken)
	{
		var id = new TenantId(tenantId);
		if (await tenants.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
		{
			return Error.NotFound("tenant.not_found", $"Tenant {tenantId} does not exist.");
		}

		tenantContext.Set(id);
		var list = await leads.ListAsync(MaxResults, cancellationToken).ConfigureAwait(false);
		return Result<IReadOnlyList<LeadResponse>>.Success([.. list.Select(LeadResponse.From)]);
	}
}
