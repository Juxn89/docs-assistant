using System.Text.RegularExpressions;

namespace DocsAssistant.Domain.Documents;

/// <summary>
/// Splits passage text into sentences for extractive answers. Conservative on purpose: it splits
/// only after a terminator followed by whitespace and an uppercase letter, an opening question or
/// exclamation mark, a quote or a digit — so "e.g. this", "v1.2.3" and "3.5 days" stay whole.
/// </summary>
public static partial class SentenceSplitter
{
	/// <summary>Shorter fragments ("Yes.", "See below.") carry no answer on their own.</summary>
	public const int MinimumSentenceLength = 20;

	public static IReadOnlyList<string> Split(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return
		[
			.. Boundary().Split(text)
				.Select(sentence => sentence.Trim())
				.Where(sentence => sentence.Length >= MinimumSentenceLength),
		];
	}

	[GeneratedRegex(@"(?<=[.!?…])\s+(?=[\p{Lu}¿¡""“\d])")]
	private static partial Regex Boundary();
}
