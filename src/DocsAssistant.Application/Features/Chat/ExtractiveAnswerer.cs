using System.ComponentModel.DataAnnotations;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Documents;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Chat;

public sealed class ExtractiveOptions
{
	public const string SectionName = "Extractive";

	[Range(1, 10)]
	public int MaxSentences { get; set; } = 3;

	/// <summary>
	/// Sentences within this cosine distance of the best one are kept; further ones are left out so a
	/// strong match is not padded with weak ones.
	/// </summary>
	[Range(0.0, 1.0)]
	public double ScoreMargin { get; set; } = 0.03;

	/// <summary>Bounds the embedding work per question (5 passages × ~10 sentences in practice).</summary>
	[Range(1, 500)]
	public int MaxCandidateSentences { get; set; } = 80;
}

/// <summary>A sentence picked from a passage, with the citation number of that passage.</summary>
public sealed record ExtractedSentence(int Citation, int Position, string Text, double Score);

/// <summary>
/// Answers without a language model: picks the sentences of the retrieved passages closest in meaning
/// to the question and returns them verbatim, each with its citation. It cannot invent anything —
/// every word is the tenant's own — which makes it the safe fallback when no model is available, and
/// the zero-cost mode of a public demo. It quotes rather than writes: a Spanish question over an
/// English document gets the English sentence.
/// </summary>
public sealed class ExtractiveAnswerer(IEmbedder embedder, IOptions<ExtractiveOptions> options)
{
	public async Task<IReadOnlyList<ExtractedSentence>> SelectAsync(
		string question,
		IReadOnlyList<RetrievedChunk> passages,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(passages);
		ArgumentNullException.ThrowIfNull(options);
		var settings = options.Value;

		var candidates = passages
			.SelectMany((passage, index) => SentencesOf(passage).Select((text, position) => (Citation: index + 1, Position: position, Text: text)))
			.Take(settings.MaxCandidateSentences)
			.ToList();
		if (candidates.Count == 0)
		{
			return [];
		}

		var query = await embedder.EmbedQueryAsync(question, cancellationToken).ConfigureAwait(false);
		var vectors = await embedder.EmbedPassagesAsync([.. candidates.Select(c => c.Text)], cancellationToken).ConfigureAwait(false);

		var scored = candidates
			.Select((c, i) => new ExtractedSentence(c.Citation, c.Position, c.Text, Dot(query, vectors[i])))
			.OrderByDescending(s => s.Score)
			.ToList();
		var best = scored[0].Score;

		// Best matches, then put back in reading order so the answer reads like the source.
		return
		[
			.. scored
				.Where(s => s.Score >= best - settings.ScoreMargin)
				.Take(settings.MaxSentences)
				.OrderBy(s => s.Citation)
				.ThenBy(s => s.Position),
		];
	}

	public static string Compose(IReadOnlyList<ExtractedSentence> sentences)
	{
		ArgumentNullException.ThrowIfNull(sentences);
		return string.Join(' ', sentences.Select(s => $"{s.Text} [{s.Citation}]"));
	}

	private static IReadOnlyList<string> SentencesOf(RetrievedChunk passage)
	{
		var sentences = SentenceSplitter.Split(passage.Text);
		return sentences.Count > 0 ? sentences : [passage.Text.Trim()];
	}

	/// <summary>The embedder returns unit vectors, so the dot product is the cosine similarity.</summary>
	private static double Dot(float[] a, float[] b)
	{
		var sum = 0.0;
		for (var i = 0; i < a.Length; i++)
		{
			sum += a[i] * b[i];
		}

		return sum;
	}
}
