using DocsAssistant.Domain.Common;

namespace DocsAssistant.Domain.Documents;

/// <summary>
/// Picks between Spanish and English by counting very common function words. It only has to choose
/// the full-text stemmer, so a cheap heuristic is enough; when the evidence is thin it keeps the
/// fallback (the tenant's default language) instead of guessing.
/// </summary>
public static class LanguageGuesser
{
	/// <summary>Below this many function-word hits the text is too short or too technical to call.</summary>
	public const int MinimumEvidence = 5;

	/// <summary>One language must outscore the other by this factor to win.</summary>
	public const double DominanceRatio = 1.5;

	private static readonly HashSet<string> Spanish = new(StringComparer.OrdinalIgnoreCase)
	{
		"el", "la", "los", "las", "de", "del", "que", "y", "en", "un", "una", "por", "con", "para",
		"es", "se", "su", "al", "lo", "como", "más", "pero", "sus", "le", "ya", "o", "este", "sí",
		"porque", "esta", "entre", "cuando", "muy", "sin", "sobre", "también", "me", "hasta", "hay",
	};

	private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
	{
		"the", "of", "and", "to", "in", "is", "you", "that", "it", "he", "was", "for", "on", "are",
		"as", "with", "his", "they", "at", "be", "this", "have", "from", "or", "by", "but", "not",
		"what", "all", "were", "we", "when", "your", "can", "there", "an", "which", "their", "will",
	};

	public static ContentLanguage Guess(string text, ContentLanguage fallback)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return fallback;
		}

		var spanish = 0;
		var english = 0;
		foreach (var word in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
		{
			if (Spanish.Contains(word))
			{
				spanish++;
			}

			if (English.Contains(word))
			{
				english++;
			}
		}

		if (spanish + english < MinimumEvidence)
		{
			return fallback;
		}

		if (spanish >= english * DominanceRatio)
		{
			return ContentLanguage.Spanish;
		}

		return english >= spanish * DominanceRatio ? ContentLanguage.English : fallback;
	}

	private static readonly char[] Separators =
		[' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '¿', '¡', '(', ')', '"', '\'', '«', '»', '—', '-'];
}
