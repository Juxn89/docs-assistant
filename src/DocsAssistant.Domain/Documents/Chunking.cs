using System.Text.RegularExpressions;

namespace DocsAssistant.Domain.Documents;

public enum TextBlockKind
{
	Heading = 0,
	Paragraph = 1,
}

/// <summary>
/// The extractor's output: text that still knows its structure. <paramref name="Level"/> is the
/// heading depth (1 = top level) and is ignored for paragraphs.
/// </summary>
public sealed record TextBlock(TextBlockKind Kind, string Text, int Level = 0)
{
	public static TextBlock Heading(string text, int level) => new(TextBlockKind.Heading, text, level);

	public static TextBlock Paragraph(string text) => new(TextBlockKind.Paragraph, text);
}

/// <summary>A chunk before it has an embedding.</summary>
public sealed record ChunkDraft(int Ordinal, string? Heading, string Text, int EstimatedTokens);

/// <summary>
/// Chunk sizing. The ceiling comes from the embedding model, not from taste:
/// multilingual-e5-small truncates at 512 tokens, and the estimate below is approximate, so the
/// default leaves headroom for the heading prefix and for estimation error.
/// </summary>
public sealed record ChunkingOptions
{
	public const int DefaultMaxTokens = 350;

	/// <summary>~15% of <see cref="DefaultMaxTokens"/>: enough to carry a sentence across a boundary.</summary>
	public const int DefaultOverlapTokens = 50;

	public static readonly ChunkingOptions Default = new(DefaultMaxTokens, DefaultOverlapTokens);

	public ChunkingOptions(int maxTokens, int overlapTokens)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
		ArgumentOutOfRangeException.ThrowIfNegative(overlapTokens);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapTokens, maxTokens);
		MaxTokens = maxTokens;
		OverlapTokens = overlapTokens;
	}

	public int MaxTokens { get; }

	public int OverlapTokens { get; }
}

/// <summary>
/// Cheap, deterministic token estimate (~4 characters per token for English and Spanish prose).
/// Chunking only needs to stay under a ceiling, so a real tokenizer here would buy precision nobody
/// uses at the cost of coupling the domain to a model's vocabulary.
/// </summary>
public static class TokenEstimator
{
	private const int CharactersPerToken = 4;

	public static int Estimate(string text) => string.IsNullOrEmpty(text) ? 0 : Estimate(text.Length);

	public static int Estimate(int characterCount) =>
		characterCount <= 0 ? 0 : (characterCount + CharactersPerToken - 1) / CharactersPerToken;
}

/// <summary>
/// Structure-aware chunking. Headings are hard boundaries — a chunk never mixes two sections, so a
/// citation points at one coherent place. Inside a section, paragraphs are packed up to the token
/// ceiling with a sentence-level overlap; a paragraph that alone exceeds the ceiling is split by
/// sentences, and a sentence that alone exceeds it is split by words.
/// </summary>
public static partial class TextChunker
{
	private const string HeadingSeparator = " › ";

	public static IReadOnlyList<ChunkDraft> Chunk(IEnumerable<TextBlock> blocks, ChunkingOptions options)
	{
		ArgumentNullException.ThrowIfNull(blocks);
		ArgumentNullException.ThrowIfNull(options);

		var chunks = new List<ChunkDraft>();
		var headings = new List<(int Level, string Text)>();
		var buffer = new List<string>();

		foreach (var block in blocks)
		{
			var text = Normalize(block.Text);
			if (text.Length == 0)
			{
				continue;
			}

			if (block.Kind == TextBlockKind.Heading)
			{
				Flush(chunks, buffer, CurrentHeading(headings));
				buffer.Clear();
				var level = Math.Max(1, block.Level);
				headings.RemoveAll(h => h.Level >= level);
				headings.Add((level, text));
				continue;
			}

			foreach (var unit in SplitToFit(text, options.MaxTokens))
			{
				if (buffer.Count > 0 && TokensOf(buffer) + TokenEstimator.Estimate(unit) > options.MaxTokens)
				{
					Flush(chunks, buffer, CurrentHeading(headings));
					var overlap = TrailingOverlap(buffer, options.OverlapTokens);
					buffer.Clear();
					if (TokensOf(overlap) + TokenEstimator.Estimate(unit) <= options.MaxTokens)
					{
						buffer.AddRange(overlap);
					}
				}

				buffer.Add(unit);
			}
		}

		Flush(chunks, buffer, CurrentHeading(headings));
		return chunks;
	}

	private static void Flush(List<ChunkDraft> chunks, List<string> buffer, string? heading)
	{
		if (buffer.Count == 0)
		{
			return;
		}

		var text = string.Join(' ', buffer);
		chunks.Add(new ChunkDraft(chunks.Count, heading, text, TokenEstimator.Estimate(text)));
	}

	/// <summary>The last whole sentences of the previous chunk that fit in the overlap budget.</summary>
	private static List<string> TrailingOverlap(List<string> previous, int overlapTokens)
	{
		var sentences = previous.SelectMany(SplitSentences).ToList();
		var overlap = new List<string>();
		var tokens = 0;
		for (var i = sentences.Count - 1; i > 0; i--)
		{
			var cost = TokenEstimator.Estimate(sentences[i]);
			if (tokens + cost > overlapTokens)
			{
				break;
			}

			overlap.Insert(0, sentences[i]);
			tokens += cost;
		}

		return overlap;
	}

	private static IEnumerable<string> SplitToFit(string paragraph, int maxTokens)
	{
		if (TokenEstimator.Estimate(paragraph) <= maxTokens)
		{
			yield return paragraph;
			yield break;
		}

		foreach (var sentence in SplitSentences(paragraph))
		{
			if (TokenEstimator.Estimate(sentence) <= maxTokens)
			{
				yield return sentence;
				continue;
			}

			foreach (var window in SplitWords(sentence, maxTokens))
			{
				yield return window;
			}
		}
	}

	private static IEnumerable<string> SplitWords(string sentence, int maxTokens)
	{
		var window = new List<string>();
		var length = 0;
		foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (window.Count > 0 && TokenEstimator.Estimate(length + 1 + word.Length) > maxTokens)
			{
				yield return string.Join(' ', window);
				window.Clear();
				length = 0;
			}

			length += (window.Count > 0 ? 1 : 0) + word.Length;
			window.Add(word);
		}

		if (window.Count > 0)
		{
			yield return string.Join(' ', window);
		}
	}

	private static IEnumerable<string> SplitSentences(string text) =>
		SentenceBoundary().Split(text).Where(s => s.Length > 0);

	private static int TokensOf(List<string> units) =>
		units.Count == 0 ? 0 : TokenEstimator.Estimate(string.Join(' ', units));

	private static string? CurrentHeading(List<(int Level, string Text)> headings) =>
		headings.Count == 0 ? null : string.Join(HeadingSeparator, headings.Select(h => h.Text));

	private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	/// <summary>Split after a sentence terminator (. ! ? …) followed by whitespace.</summary>
	[GeneratedRegex(@"(?<=[.!?…])\s+")]
	private static partial Regex SentenceBoundary();
}
