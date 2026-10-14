using DocsAssistant.Domain.Common;

namespace DocsAssistant.Domain.Handoff;

public enum LeadStatus
{
	/// <summary>Waiting to be sent, now or at <see cref="Lead.NextAttemptAt"/>.</summary>
	Pending = 0,
	Sending = 1,
	Notified = 2,
	/// <summary>Every attempt failed. The lead is kept; the operator still sees it in the admin API.</summary>
	Failed = 3,
}

/// <summary>
/// A visitor asking for a person: their contact, their message and the conversation it came from.
/// It is stored before anyone is notified, so a mail server being down delays the email but never
/// loses the request. Sending is the same small state machine as ingestion:
/// Pending → Sending → Notified, or back to Pending with a later <see cref="NextAttemptAt"/>, or Failed
/// once attempts run out.
/// </summary>
public sealed class Lead
{
	public const int EmailMaxLength = 320;
	public const int NameMaxLength = 100;
	public const int MessageMaxLength = 2000;
	public const int ErrorMaxLength = 1000;

	private Lead()
	{
		VisitorEmail = string.Empty;
		Message = string.Empty;
	}

	public LeadId Id { get; private set; }

	public TenantId TenantId { get; private set; }

	/// <summary>Null when the visitor wrote without having chatted, or the conversation no longer exists.</summary>
	public ConversationId? ConversationId { get; private set; }

	public string VisitorEmail { get; private set; }

	public string? VisitorName { get; private set; }

	public string Message { get; private set; }

	/// <summary>The language the visitor wrote in; the operator's email is in the tenant's language.</summary>
	public ContentLanguage Language { get; private set; }

	public LeadStatus Status { get; private set; }

	public int Attempts { get; private set; }

	public string? LastError { get; private set; }

	public DateTimeOffset CreatedAt { get; private set; }

	public DateTimeOffset NextAttemptAt { get; private set; }

	/// <summary>When the current attempt claimed the lead. Acts as a lease.</summary>
	public DateTimeOffset? SendingStartedAt { get; private set; }

	public DateTimeOffset? NotifiedAt { get; private set; }

	/// <summary>Inputs are validated by the use case; these checks only stop a bug from storing garbage.</summary>
	public static Lead Create(
		TenantId tenantId,
		ConversationId? conversationId,
		string visitorEmail,
		string? visitorName,
		string message,
		ContentLanguage language,
		DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(visitorEmail);
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(visitorEmail.Length, EmailMaxLength);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(message.Length, MessageMaxLength);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(visitorName?.Length ?? 0, NameMaxLength);

		return new Lead
		{
			Id = LeadId.New(),
			TenantId = tenantId,
			ConversationId = conversationId,
			VisitorEmail = visitorEmail,
			VisitorName = string.IsNullOrWhiteSpace(visitorName) ? null : visitorName,
			Message = message,
			Language = language,
			Status = LeadStatus.Pending,
			CreatedAt = now,
			NextAttemptAt = now,
		};
	}

	public bool IsDue(DateTimeOffset now) => Status == LeadStatus.Pending && NextAttemptAt <= now;

	public void StartSending(DateTimeOffset now)
	{
		if (!IsDue(now))
		{
			throw new DomainException($"Cannot send lead {Id}: it is {Status}, next attempt at {NextAttemptAt:O}.");
		}

		Status = LeadStatus.Sending;
		Attempts++;
		SendingStartedAt = now;
	}

	public void MarkNotified(DateTimeOffset now)
	{
		EnsureSending(nameof(MarkNotified));
		Status = LeadStatus.Notified;
		LastError = null;
		NotifiedAt = now;
	}

	/// <summary>
	/// Records a failed attempt. Returns <see langword="true"/> when the lead went back to Pending, due
	/// again after <paramref name="retryDelay"/>; <see langword="false"/> when it is now Failed.
	/// </summary>
	public bool RecordFailure(string reason, int maxAttempts, TimeSpan retryDelay, DateTimeOffset now)
	{
		EnsureSending(nameof(RecordFailure));
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

		LastError = reason.Length <= ErrorMaxLength ? reason : reason[..ErrorMaxLength];
		if (Attempts < maxAttempts)
		{
			Status = LeadStatus.Pending;
			NextAttemptAt = now + retryDelay;
			return true;
		}

		Status = LeadStatus.Failed;
		return false;
	}

	/// <summary>
	/// A worker that died while sending leaves the lead in Sending. Once the claim is older than the
	/// lease it goes back to Pending; the abandoned attempt counts, so a lead that crashes the sender
	/// every time ends Failed instead of looping. The email may have gone out before the crash: a
	/// duplicate is the price of never losing a request.
	/// </summary>
	public bool ReleaseExpiredClaim(DateTimeOffset now, TimeSpan leaseDuration, int maxAttempts)
	{
		if (Status != LeadStatus.Sending || SendingStartedAt is not { } started || now - started < leaseDuration)
		{
			return false;
		}

		LastError = "The previous sending attempt did not finish (worker restarted or crashed).";
		Status = Attempts < maxAttempts ? LeadStatus.Pending : LeadStatus.Failed;
		NextAttemptAt = now;
		return true;
	}

	private void EnsureSending(string operation)
	{
		if (Status != LeadStatus.Sending)
		{
			throw new DomainException($"Cannot {operation} lead {Id}: it is {Status}, expected {LeadStatus.Sending}.");
		}
	}
}
