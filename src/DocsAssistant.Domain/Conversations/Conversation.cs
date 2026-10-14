using DocsAssistant.Domain.Common;

namespace DocsAssistant.Domain.Conversations;

public enum MessageRole
{
	Visitor = 0,
	Assistant = 1,
}

/// <summary>A source shown under an answer, kept with the message so the transcript reads as the visitor saw it.</summary>
public sealed record MessageCitation(int Number, string Title, string Source);

/// <summary>
/// One visitor's exchange with the assistant, anonymous: the widget keeps the id, nothing identifies
/// the person until they ask for a human and leave an email. It exists so a handoff carries what was
/// already asked, and so the operator can see what the assistant could not answer.
///
/// Messages are separate rows rather than a collection loaded with the conversation: recording a
/// turn never reads the history, and the transcript reads only the latest messages.
/// </summary>
public sealed class Conversation
{
	private Conversation()
	{
	}

	public ConversationId Id { get; private set; }

	public TenantId TenantId { get; private set; }

	public DateTimeOffset StartedAt { get; private set; }

	public DateTimeOffset LastMessageAt { get; private set; }

	public static Conversation Start(TenantId tenantId, DateTimeOffset now) => new()
	{
		Id = ConversationId.New(),
		TenantId = tenantId,
		StartedAt = now,
		LastMessageAt = now,
	};

	public ConversationMessage AddVisitorMessage(string text, DateTimeOffset now) =>
		Add(MessageRole.Visitor, text, outcome: null, [], now);

	/// <param name="outcome">How the answer ended (answered, abstained, refused…), as the application names it.</param>
	public ConversationMessage AddAssistantMessage(string text, string outcome, IEnumerable<MessageCitation> citations, DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
		ArgumentNullException.ThrowIfNull(citations);
		return Add(MessageRole.Assistant, text, outcome, [.. citations], now);
	}

	private ConversationMessage Add(MessageRole role, string text, string? outcome, IReadOnlyList<MessageCitation> citations, DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		LastMessageAt = now;
		return new ConversationMessage(Id, TenantId, role, ConversationMessage.Truncate(text.Trim()), outcome, citations, now);
	}
}

public sealed class ConversationMessage
{
	/// <summary>Bounds storage per row. An answer is capped by the model's output tokens far below this.</summary>
	public const int MaxTextLength = 8000;

	private const string Ellipsis = "…";

	private ConversationMessage()
	{
		Text = string.Empty;
		Citations = [];
	}

	internal ConversationMessage(
		ConversationId conversationId,
		TenantId tenantId,
		MessageRole role,
		string text,
		string? outcome,
		IReadOnlyList<MessageCitation> citations,
		DateTimeOffset createdAt)
	{
		Id = MessageId.New();
		ConversationId = conversationId;
		TenantId = tenantId;
		Role = role;
		Text = text;
		Outcome = outcome;
		Citations = citations;
		CreatedAt = createdAt;
	}

	public MessageId Id { get; private set; }

	public ConversationId ConversationId { get; private set; }

	public TenantId TenantId { get; private set; }

	public MessageRole Role { get; private set; }

	public string Text { get; private set; }

	/// <summary>Set on assistant messages only.</summary>
	public string? Outcome { get; private set; }

	public IReadOnlyList<MessageCitation> Citations { get; private set; }

	public DateTimeOffset CreatedAt { get; private set; }

	internal static string Truncate(string text) =>
		text.Length <= MaxTextLength ? text : string.Concat(text.AsSpan(0, MaxTextLength - Ellipsis.Length), Ellipsis);
}
