using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Handoff;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Handoff;

public enum NotifyOutcome
{
	Notified = 0,
	/// <summary>Failed this time; back to Pending with a later due time.</summary>
	WillRetry = 1,
	Failed = 2,
	/// <summary>Not ours to send: unknown, not due yet, or already claimed by another run.</summary>
	Skipped = 3,
}

/// <summary>Sends one lead to the tenant's operator. Runs in the background worker; never in a request.</summary>
public sealed partial class NotifyLead(
	ITenantContext tenantContext,
	ITenantRepository tenants,
	ILeadRepository leads,
	IConversationRepository conversations,
	IHandoffNotifier notifier,
	IOptions<HandoffOptions> options,
	TimeProvider clock,
	ILogger<NotifyLead> logger)
{
	public async Task<NotifyOutcome> HandleAsync(LeadJob job, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(job);
		tenantContext.Set(job.TenantId);
		var settings = options.Value;

		var lead = await leads.GetAsync(job.LeadId, cancellationToken).ConfigureAwait(false);
		if (lead is null || !lead.IsDue(clock.GetUtcNow()))
		{
			return NotifyOutcome.Skipped;
		}

		try
		{
			// Claiming is a write: if another worker claimed it first, the concurrency token rejects this one.
			lead.StartSending(clock.GetUtcNow());
			await leads.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (ConcurrencyConflictException)
		{
			return NotifyOutcome.Skipped;
		}

		try
		{
			var tenant = await tenants.GetAsync(job.TenantId, cancellationToken).ConfigureAwait(false)
				?? throw new InvalidOperationException($"Tenant {job.TenantId} of lead {lead.Id} does not exist.");
			IReadOnlyList<ConversationMessage> transcript = lead.ConversationId is { } conversationId && settings.TranscriptMessages > 0
				? await conversations.ListRecentMessagesAsync(conversationId, settings.TranscriptMessages, cancellationToken).ConfigureAwait(false)
				: [];

			await notifier.SendAsync(LeadEmail.Compose(tenant, lead, transcript), cancellationToken).ConfigureAwait(false);

			lead.MarkNotified(clock.GetUtcNow());
			await leads.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
			LogNotified(logger, lead.Id.Value, lead.Attempts);
			return NotifyOutcome.Notified;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			var willRetry = lead.RecordFailure(Describe(exception), settings.MaxAttempts, RetryDelay(lead.Attempts, settings), clock.GetUtcNow());
			await leads.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
			LogFailed(logger, exception, lead.Id.Value, lead.Attempts, willRetry);
			return willRetry ? NotifyOutcome.WillRetry : NotifyOutcome.Failed;
		}
	}

	/// <summary>1, 2, 4, 8… times the base delay, capped: a mail server that is down for a while is not hammered.</summary>
	internal static TimeSpan RetryDelay(int attempt, HandoffOptions settings)
	{
		var factor = Math.Pow(2, Math.Max(0, attempt - 1));
		var delay = settings.RetryDelay * factor;
		return delay < settings.MaxRetryDelay ? delay : settings.MaxRetryDelay;
	}

	/// <summary>Stored on the lead and shown to the admin; never a stack trace.</summary>
	private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

	[LoggerMessage(Level = LogLevel.Information, Message = "Lead {LeadId} notified on attempt {Attempt}")]
	private static partial void LogNotified(ILogger logger, Guid leadId, int attempt);

	[LoggerMessage(Level = LogLevel.Error, Message = "Lead {LeadId} could not be notified on attempt {Attempt}; will retry: {WillRetry}")]
	private static partial void LogFailed(ILogger logger, Exception exception, Guid leadId, int attempt, bool willRetry);
}
