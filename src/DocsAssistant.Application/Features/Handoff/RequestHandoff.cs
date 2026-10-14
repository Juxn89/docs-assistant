using System.Net.Mail;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Handoff;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Handoff;

public sealed record HandoffRequest(Guid? ConversationId, string? Email, string? Name, string? Message);

public sealed record HandoffAccepted(Guid LeadId);

/// <summary>
/// The visitor asks for a person. The lead is stored first and the operator notified in the
/// background, so the visitor gets an immediate answer and a mail outage loses nothing.
/// </summary>
public sealed partial class RequestHandoff(
	ITenantContext tenantContext,
	ILeadRepository leads,
	IConversationRepository conversations,
	ILeadQueue queue,
	IOptions<HandoffOptions> options,
	TimeProvider clock,
	ILogger<RequestHandoff> logger)
{
	public async Task<Result<HandoffAccepted>> HandleAsync(
		TenantId tenantId,
		ContentLanguage tenantLanguage,
		HandoffRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var email = request.Email?.Trim() ?? string.Empty;
		var name = request.Name?.Trim();
		var message = request.Message?.Trim() ?? string.Empty;
		if (Validate(email, name, message) is { } invalid)
		{
			return invalid;
		}

		tenantContext.Set(tenantId);
		var now = clock.GetUtcNow();

		var duplicate = await leads.FindDuplicateAsync(email, message, now - options.Value.DuplicateWindow, cancellationToken).ConfigureAwait(false);
		if (duplicate is not null)
		{
			return new HandoffAccepted(duplicate.Id.Value);
		}

		// Attached only if it is this tenant's: the repository cannot see anyone else's.
		ConversationId? conversationId = null;
		if (request.ConversationId is { } id
			&& await conversations.GetAsync(new ConversationId(id), cancellationToken).ConfigureAwait(false) is { } conversation)
		{
			conversationId = conversation.Id;
		}

		var lead = Lead.Create(tenantId, conversationId, email, name, message, LanguageGuesser.Guess(message, tenantLanguage), now);
		leads.Add(lead);
		await leads.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await queue.EnqueueAsync(new LeadJob(tenantId, lead.Id), cancellationToken).ConfigureAwait(false);

		// The id only: the email address and the message are personal data and stay out of the logs.
		LogLeadCreated(logger, lead.Id.Value, tenantId.Value);
		return new HandoffAccepted(lead.Id.Value);
	}

	private static Error? Validate(string email, string? name, string message)
	{
		// MailAddress accepts display-name forms ("Ann <a@b.c>") and is lenient with whitespace; the
		// address must be exactly what was parsed, with no line breaks that could reach a mail header.
		if (email.Length is 0 or > Lead.EmailMaxLength
			|| !MailAddress.TryCreate(email, out var parsed)
			|| !string.Equals(parsed.Address, email, StringComparison.Ordinal)
			|| email.AsSpan().ContainsAny('\r', '\n'))
		{
			return Error.Validation("handoff.invalid_email", "Enter a valid email address so we can reply.");
		}

		if (name is { Length: > Lead.NameMaxLength } || (name is not null && name.AsSpan().ContainsAny('\r', '\n')))
		{
			return Error.Validation("handoff.invalid_name", $"The name must be a single line of at most {Lead.NameMaxLength} characters.");
		}

		if (message.Length == 0)
		{
			return Error.Validation("handoff.message_required", "Write a message for the team.");
		}

		if (message.Length > Lead.MessageMaxLength)
		{
			return Error.Validation("handoff.message_too_long", $"The message can be at most {Lead.MessageMaxLength} characters.");
		}

		return null;
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Lead {LeadId} created for tenant {TenantId}")]
	private static partial void LogLeadCreated(ILogger logger, Guid leadId, Guid tenantId);
}
