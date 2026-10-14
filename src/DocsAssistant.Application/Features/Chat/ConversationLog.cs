using System.Runtime.CompilerServices;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Chat;

/// <summary>
/// Records each turn of the chat: the question when it arrives, the answer when it completes. The
/// widget sends back the conversation id it was given; an unknown id (expired, another tenant's,
/// made up) silently starts a new conversation rather than failing the question.
/// </summary>
public sealed class ConversationLog(
	ITenantContext tenantContext,
	IConversationRepository conversations,
	IOptions<ChatAnswerOptions> options,
	TimeProvider clock)
{
	public async Task<Conversation> BeginTurnAsync(TenantId tenantId, Guid? conversationId, string question, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(question);
		tenantContext.Set(tenantId);

		var conversation = conversationId is { } id
			? await conversations.GetAsync(new ConversationId(id), cancellationToken).ConfigureAwait(false)
			: null;
		if (conversation is null)
		{
			conversation = Conversation.Start(tenantId, clock.GetUtcNow());
			conversations.Add(conversation);
		}

		// Stored as the assistant read it: AskQuestion truncates to the same length.
		var text = question.Trim();
		var max = options.Value.MaxQuestionLength;
		conversations.Add(conversation.AddVisitorMessage(text.Length <= max ? text : text[..max], clock.GetUtcNow()));
		await conversations.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return conversation;
	}

	/// <summary>
	/// Passes the answer through and records it once it completes. An answer the visitor abandoned
	/// mid-stream is not recorded; their question is.
	/// </summary>
	public async IAsyncEnumerable<AnswerEvent> RecordAsync(
		Conversation conversation,
		IAsyncEnumerable<AnswerEvent> answer,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(conversation);
		ArgumentNullException.ThrowIfNull(answer);

		await foreach (var e in answer.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			if (e is AnswerCompleted done)
			{
				conversations.Add(conversation.AddAssistantMessage(
					done.Answer,
					done.Outcome.ToString(),
					done.Citations.Select(c => new MessageCitation(c.Number, c.DocumentTitle, c.SourceUri)),
					clock.GetUtcNow()));
				await conversations.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			}

			yield return e;
		}
	}
}
