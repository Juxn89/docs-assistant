using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Handoff;
using Microsoft.EntityFrameworkCore;

namespace DocsAssistant.Infrastructure.Persistence;

/// <summary>Every query here goes through the tenant query filter; nothing calls IgnoreQueryFilters.</summary>
internal sealed class ConversationRepository(DocsAssistantDbContext db) : IConversationRepository
{
	public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken) =>
		db.Conversations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

	public void Add(Conversation conversation) => db.Conversations.Add(conversation);

	public void Add(ConversationMessage message) => db.Messages.Add(message);

	public async Task<IReadOnlyList<ConversationMessage>> ListRecentMessagesAsync(ConversationId id, int limit, CancellationToken cancellationToken)
	{
		// Message ids are version 7 GUIDs: they break ties between messages recorded in the same instant.
		var latest = await db.Messages
			.AsNoTracking()
			.Where(m => m.ConversationId == id)
			.OrderByDescending(m => m.CreatedAt)
			.ThenByDescending(m => m.Id)
			.Take(limit)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		latest.Reverse();
		return latest;
	}

	public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>Every query here goes through the tenant query filter; nothing calls IgnoreQueryFilters.</summary>
internal sealed class LeadRepository(DocsAssistantDbContext db) : ILeadRepository
{
	public Task<Lead?> GetAsync(LeadId id, CancellationToken cancellationToken) =>
		db.Leads.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

	public Task<Lead?> FindDuplicateAsync(string visitorEmail, string message, DateTimeOffset since, CancellationToken cancellationToken) =>
		db.Leads
			.Where(l => l.VisitorEmail == visitorEmail && l.Message == message && l.CreatedAt >= since)
			.OrderByDescending(l => l.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken);

	public async Task<IReadOnlyList<Lead>> ListAsync(int limit, CancellationToken cancellationToken) =>
		await db.Leads.AsNoTracking().OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);

	public void Add(Lead lead) => db.Leads.Add(lead);

	public async Task SaveChangesAsync(CancellationToken cancellationToken)
	{
		try
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException exception)
		{
			throw new ConcurrencyConflictException("The lead was modified by another operation.", exception);
		}
	}
}

/// <summary>
/// The worker's recovery sweep over leads, the second deliberate cross-tenant read. It returns ids
/// only, and the worker binds each job's tenant before touching the lead.
/// </summary>
internal sealed class PendingLeadSource(DocsAssistantDbContext db, TimeProvider clock) : IPendingLeadSource
{
	public async Task<IReadOnlyList<LeadJob>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
	{
		var due = await db.Leads
			.IgnoreQueryFilters()
			.AsNoTracking()
			.Where(l => l.Status == LeadStatus.Pending && l.NextAttemptAt <= now)
			.OrderBy(l => l.NextAttemptAt)
			.Take(limit)
			.Select(l => new { l.TenantId, l.Id })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		return [.. due.Select(d => new LeadJob(d.TenantId, d.Id))];
	}

	public async Task<int> ReleaseExpiredClaimsAsync(TimeSpan lease, int maxAttempts, CancellationToken cancellationToken)
	{
		var now = clock.GetUtcNow();
		var cutoff = now - lease;
		var stale = await db.Leads
			.IgnoreQueryFilters()
			.Where(l => l.Status == LeadStatus.Sending && l.SendingStartedAt < cutoff)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		var released = stale.Count(l => l.ReleaseExpiredClaim(now, lease, maxAttempts));
		try
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException)
		{
			// The "dead" worker finished after all. Its result wins; the next sweep re-evaluates.
			return 0;
		}

		return released;
	}
}
