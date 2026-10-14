using System.ComponentModel.DataAnnotations;

namespace DocsAssistant.Application.Features.Chat;

public enum AnswerOutcome
{
	/// <summary>Answered from the documents, with at least one valid citation.</summary>
	Answered = 0,

	/// <summary>The model answered but cited nothing valid: shown, flagged, and handoff offered.</summary>
	AnsweredWithoutCitations = 1,

	/// <summary>Retrieval found nothing close enough; the model was never called.</summary>
	AbstainedNoContext = 2,

	/// <summary>The model read the passages and said they do not contain the answer.</summary>
	AbstainedByModel = 3,

	/// <summary>No model was available: the most relevant sentences, quoted verbatim with citations.</summary>
	AnsweredExtractively = 4,
}

/// <summary>A passage the answer may cite, numbered as the model saw it.</summary>
public sealed record AnswerSource(int Number, Guid ChunkId, string DocumentTitle, string SourceUri, string? Heading, string Excerpt);

public sealed record TokenUsage(long InputTokens, long OutputTokens, long CachedInputTokens);

/// <summary>What the stream emits, in order: zero or more deltas, then exactly one completion.</summary>
public abstract record AnswerEvent;

public sealed record AnswerDelta(string Text) : AnswerEvent;

public sealed record AnswerCompleted(
	string Answer,
	AnswerOutcome Outcome,
	IReadOnlyList<AnswerSource> Citations,
	bool HadInvalidCitations,
	double TopSimilarity,
	string PromptHash,
	TokenUsage? Usage) : AnswerEvent
{
	/// <summary>
	/// Every outcome but a written, cited answer is a moment to offer a human — including quoted
	/// (extractive) answers: they cannot tell whether what they quote actually answers the question.
	/// </summary>
	public bool OfferHandoff => Outcome != AnswerOutcome.Answered;
}

public sealed class ChatAnswerOptions
{
	public const string SectionName = "Chat";

	/// <summary>A support answer is a few sentences; this caps cost and latency of a runaway reply.</summary>
	[Range(64, 4096)]
	public int MaxOutputTokens { get; set; } = 600;

	/// <summary>Passage text shown with a citation in the UI.</summary>
	[Range(40, 1000)]
	public int ExcerptLength { get; set; } = 240;

	[Range(1, 4000)]
	public int MaxQuestionLength { get; set; } = 1000;

	/// <summary>
	/// When the model cannot be used and nothing was streamed yet, answer by quoting the passages
	/// instead of failing. Off only where an error is preferable to a quoted answer.
	/// </summary>
	public bool ExtractiveFallback { get; set; } = true;
}

/// <summary>Fixed, user-facing answers the system gives on its own, in both supported languages.</summary>
public static class AnswerTexts
{
	public const string NoAnswerSentinel = "NO_ANSWER";

	public static string NoInformation(bool spanish) => spanish
		? "No encuentro esa información en los documentos que tengo. ¿Quieres que le pase tu pregunta a una persona?"
		: "I can't find that in the documents I have. Would you like me to pass your question to a person?";

	/// <summary>
	/// Frames quoted sentences as what they are. Measured on the evals: neither passage nor sentence
	/// similarity can tell an unanswerable question apart, so extractive mode must not present its
	/// quotes as "the answer".
	/// </summary>
	public static string ExtractiveLeadIn(bool spanish) => spanish
		? "Esto es lo más relacionado que encontré en los documentos:"
		: "This is the most relevant information I found in the documents:";
}
