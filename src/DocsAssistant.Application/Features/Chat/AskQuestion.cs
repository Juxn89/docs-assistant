using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Chat;

/// <summary>
/// Answers a question from the tenant's documents, streaming.
///
/// Three ways to say "I don't know", from cheapest to most expensive: retrieval below the similarity
/// floor (no model call), the model replying with the no-answer sentinel after reading the passages,
/// and an answer that cites nothing valid (shown, but flagged for handoff).
///
/// When the model cannot be used — none configured, provider down, timeout — and nothing has been
/// streamed yet, the answer falls back to quoting the most relevant sentences (extractive). A model
/// failure after the first words were shown cannot be papered over and ends as an error event.
/// </summary>
public sealed partial class AskQuestion(
	RetrieveContext retrieve,
	IChatClient chat,
	ExtractiveAnswerer extractive,
	IPromptLibrary prompts,
	IOptions<ChatAnswerOptions> options,
	ILogger<AskQuestion> logger)
{
	public async IAsyncEnumerable<AnswerEvent> HandleAsync(
		TenantId tenantId,
		string question,
		ContentLanguage tenantLanguage,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		var settings = options.Value;
		question = (question ?? string.Empty).Trim();
		if (question.Length > settings.MaxQuestionLength)
		{
			question = question[..settings.MaxQuestionLength];
		}

		var spanish = LanguageGuesser.Guess(question, tenantLanguage) == ContentLanguage.Spanish;
		var prompt = prompts.Answer;

		var context = await retrieve.HandleAsync(tenantId, question, cancellationToken).ConfigureAwait(false);
		if (!context.IsConfident)
		{
			var text = AnswerTexts.NoInformation(spanish);
			yield return new AnswerDelta(text);
			yield return new AnswerCompleted(text, AnswerOutcome.AbstainedNoContext, [], false, context.TopSimilarity, prompt.Hash, Usage: null);
			yield break;
		}

		var sources = context.Chunks
			.Select((chunk, i) => new AnswerSource(i + 1, chunk.ChunkId.Value, chunk.DocumentTitle, chunk.SourceUri, chunk.Heading, Excerpt(chunk.Text, settings.ExcerptLength)))
			.ToList();

		var chatOptions = new ChatOptions
		{
			Instructions = prompt.Text,
			MaxOutputTokens = settings.MaxOutputTokens,
			Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
		};
		ChatMessage[] messages = [new(ChatRole.User, BuildUserMessage(context.Chunks, question))];

		var answer = new StringBuilder();
		var pending = new StringBuilder();
		var decided = false;
		var abstained = false;
		Exception? modelFailure = null;
		UsageDetails? usage = null;

		IAsyncEnumerator<ChatResponseUpdate>? stream = null;
		try
		{
			try
			{
				stream = chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
			}
			catch (Exception exception) when (CanFallBack(exception, settings))
			{
				modelFailure = exception;
			}

			while (stream is not null)
			{
				bool moved;
				try
				{
					moved = await stream.MoveNextAsync().ConfigureAwait(false);
				}
				catch (Exception exception) when (answer.Length == 0 && CanFallBack(exception, settings))
				{
					modelFailure = exception;
					break;
				}

				if (!moved)
				{
					break;
				}

				var update = stream.Current;
				usage = Accumulate(usage, update);
				if (string.IsNullOrEmpty(update.Text))
				{
					continue;
				}

				if (decided)
				{
					answer.Append(update.Text);
					yield return new AnswerDelta(update.Text);
					continue;
				}

				// Hold back the first characters until they cannot be the start of the sentinel, so the
				// user never sees "NO_ANS…" flash before the abstention replaces it.
				pending.Append(update.Text);
				var head = pending.ToString().TrimStart();
				if (head.StartsWith(AnswerTexts.NoAnswerSentinel, StringComparison.Ordinal))
				{
					abstained = true;
					break;
				}

				if (head.Length >= AnswerTexts.NoAnswerSentinel.Length || !AnswerTexts.NoAnswerSentinel.StartsWith(head, StringComparison.Ordinal))
				{
					decided = true;
					var released = pending.ToString();
					answer.Append(released);
					yield return new AnswerDelta(released);
				}
			}
		}
		finally
		{
			if (stream is not null)
			{
				await stream.DisposeAsync().ConfigureAwait(false);
			}
		}

		if (modelFailure is not null)
		{
			LogFallback(logger, modelFailure);
			await foreach (var fallback in AnswerExtractivelyAsync(question, context, sources, spanish, prompt.Hash, cancellationToken).ConfigureAwait(false))
			{
				yield return fallback;
			}

			yield break;
		}

		if (!decided && !abstained && pending.Length > 0)
		{
			// The whole reply was shorter than the sentinel and is not it.
			answer.Append(pending);
			yield return new AnswerDelta(pending.ToString());
		}

		var tokens = usage is null ? null : new TokenUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, usage.CachedInputTokenCount ?? 0);
		if (abstained || answer.Length == 0)
		{
			var text = AnswerTexts.NoInformation(spanish);
			yield return new AnswerDelta(text);
			yield return new AnswerCompleted(text, AnswerOutcome.AbstainedByModel, [], false, context.TopSimilarity, prompt.Hash, tokens);
			yield break;
		}

		var final = answer.ToString();
		var cited = CitationNumbers(final);
		var valid = cited.Where(n => n >= 1 && n <= sources.Count).Distinct().Order().ToList();
		var outcome = valid.Count > 0 ? AnswerOutcome.Answered : AnswerOutcome.AnsweredWithoutCitations;

		yield return new AnswerCompleted(
			final,
			outcome,
			[.. valid.Select(n => sources[n - 1])],
			HadInvalidCitations: cited.Any(n => n < 1 || n > sources.Count),
			context.TopSimilarity,
			prompt.Hash,
			tokens);
	}

	private async IAsyncEnumerable<AnswerEvent> AnswerExtractivelyAsync(
		string question,
		RetrievalResult context,
		IReadOnlyList<AnswerSource> sources,
		bool spanish,
		string promptHash,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var sentences = await extractive.SelectAsync(question, context.Chunks, cancellationToken).ConfigureAwait(false);
		if (sentences.Count == 0)
		{
			var none = AnswerTexts.NoInformation(spanish);
			yield return new AnswerDelta(none);
			yield return new AnswerCompleted(none, AnswerOutcome.AbstainedNoContext, [], false, context.TopSimilarity, promptHash, Usage: null);
			yield break;
		}

		var text = $"{AnswerTexts.ExtractiveLeadIn(spanish)} {ExtractiveAnswerer.Compose(sentences)}";
		yield return new AnswerDelta(text);
		yield return new AnswerCompleted(
			text,
			AnswerOutcome.AnsweredExtractively,
			[.. sentences.Select(s => s.Citation).Distinct().Order().Select(n => sources[n - 1])],
			HadInvalidCitations: false,
			context.TopSimilarity,
			promptHash,
			Usage: null);
	}

	private static bool CanFallBack(Exception exception, ChatAnswerOptions settings) =>
		settings.ExtractiveFallback && exception is not OperationCanceledException;

	[LoggerMessage(Level = LogLevel.Warning, Message = "Language model unavailable; answering extractively")]
	private static partial void LogFallback(ILogger logger, Exception exception);

	/// <summary>
	/// Passages are data, not instructions: each one is wrapped and its text escaped, so a document that
	/// says "ignore your instructions" cannot close the tag and speak as the operator.
	/// </summary>
	internal static string BuildUserMessage(IReadOnlyList<RetrievedChunk> chunks, string question)
	{
		var builder = new StringBuilder("<documents>\n");
		for (var i = 0; i < chunks.Count; i++)
		{
			var chunk = chunks[i];
			builder
				.Append("<document index=\"").Append(i + 1).Append("\" title=\"").Append(Escape(chunk.DocumentTitle)).Append('"');
			if (chunk.Heading is not null)
			{
				builder.Append(" section=\"").Append(Escape(chunk.Heading)).Append('"');
			}

			builder.Append(">\n").Append(Escape(chunk.Text)).Append("\n</document>\n");
		}

		return builder
			.Append("</documents>\n\n<question>\n").Append(Escape(question)).Append("\n</question>")
			.ToString();
	}

	internal static IReadOnlyList<int> CitationNumbers(string text) =>
		[.. CitationMarker().Matches(text).Select(m => int.Parse(m.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture))];

	private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

	private static string Excerpt(string text, int length) =>
		text.Length <= length ? text : string.Concat(text.AsSpan(0, length), "…");

	private static UsageDetails? Accumulate(UsageDetails? total, ChatResponseUpdate update)
	{
		foreach (var usage in update.Contents.OfType<UsageContent>())
		{
			total ??= new UsageDetails();
			total.Add(usage.Details);
		}

		return total;
	}

	[GeneratedRegex(@"\[(\d{1,2})\]")]
	private static partial Regex CitationMarker();
}
