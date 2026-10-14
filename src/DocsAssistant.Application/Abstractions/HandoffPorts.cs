using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Handoff;

namespace DocsAssistant.Application.Abstractions;

/// <summary>Tenant-scoped: a conversation id from another tenant is not found.</summary>
public interface IConversationRepository
{
	Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken);

	void Add(Conversation conversation);

	void Add(ConversationMessage message);

	/// <summary>The latest <paramref name="limit"/> messages, oldest first.</summary>
	Task<IReadOnlyList<ConversationMessage>> ListRecentMessagesAsync(ConversationId id, int limit, CancellationToken cancellationToken);

	Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Tenant-scoped, like every other repository.</summary>
public interface ILeadRepository
{
	Task<Lead?> GetAsync(LeadId id, CancellationToken cancellationToken);

	/// <summary>The same visitor sending the same message again since <paramref name="since"/>: a double submit.</summary>
	Task<Lead?> FindDuplicateAsync(string visitorEmail, string message, DateTimeOffset since, CancellationToken cancellationToken);

	Task<IReadOnlyList<Lead>> ListAsync(int limit, CancellationToken cancellationToken);

	void Add(Lead lead);

	/// <summary>Throws <see cref="Common.ConcurrencyConflictException"/> when another worker changed the lead first.</summary>
	Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A lead to notify. Carries the tenant so the worker can bind it before reading.</summary>
public sealed record LeadJob(TenantId TenantId, LeadId LeadId);

public interface ILeadQueue
{
	ValueTask EnqueueAsync(LeadJob job, CancellationToken cancellationToken);
}

/// <summary>Recovery path for the lead queue: due leads the channel lost, retries, crashed sends. Cross-tenant, ids only.</summary>
public interface IPendingLeadSource
{
	Task<IReadOnlyList<LeadJob>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

	Task<int> ReleaseExpiredClaimsAsync(TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);
}

/// <summary>A plain-text email to the tenant's operator. Plain text on purpose: visitor text is never rendered as HTML.</summary>
public sealed record HandoffEmail(string To, string ReplyTo, string? ReplyToName, string Subject, string Body);

/// <summary>Delivers a handoff. Email today; another channel (WhatsApp, a CRM webhook) is another adapter.</summary>
public interface IHandoffNotifier
{
	Task SendAsync(HandoffEmail email, CancellationToken cancellationToken);
}
