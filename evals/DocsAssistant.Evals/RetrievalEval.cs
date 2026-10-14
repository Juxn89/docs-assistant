using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Domain.Common;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocsAssistant.Evals;

internal sealed record GoldenItem(string Id, string Question, string Lang, string? Doc, string? Evidence)
{
	public bool Answerable => Doc is not null;
}

internal sealed record ModeMetrics(string Mode, int Answerable, double HitAt1, double HitAt5, double Mrr, int CrossLingual, double CrossLingualHitAt5);

/// <param name="SafeFloor">
/// Highest threshold (rounded down to 0.01) that still keeps every answerable question: below it the
/// assistant can abstain without calling the model and never refuse a question it could answer.
/// </param>
/// <param name="UnanswerableBelowFloor">How many unanswerable questions that floor alone catches.</param>
/// <param name="BestSingleThreshold">The threshold with the best balanced accuracy, for comparison.</param>
internal sealed record Calibration(
	double SafeFloor,
	int UnanswerableBelowFloor,
	int Unanswerable,
	double BestSingleThreshold,
	double BestBalancedAccuracy,
	double MinAnswerable,
	double MaxUnanswerable,
	double MeanAnswerable,
	double MeanUnanswerable);

internal sealed record ItemResult(string Id, string Mode, int? Rank, double TopSimilarity, string? TopChunk);

/// <summary>
/// Retrieval quality, with no language model involved: did the passage that contains the answer make
/// it into the top 5? Measured for vector-only, text-only and hybrid, so the value of fusing them is a
/// number in the README rather than a claim.
/// </summary>
internal static partial class RetrievalEval
{
	private const int TopK = 5;

	public static async Task<RetrievalReport> RunAsync(EvalEnvironment environment, RepoPaths repo)
	{
		Console.WriteLine("Ingesting corpus…");
		var (tenant, languages) = await environment.IngestCorpusAsync(repo.CorpusDirectory);

		var golden = (await File.ReadAllLinesAsync(Path.Combine(repo.DatasetsDirectory, "retrieval.jsonl")))
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.Select(line => JsonSerializer.Deserialize<GoldenItem>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
			.ToList();
		await ValidateGoldenAsync(environment, tenant, golden);

		var results = new List<ItemResult>();
		foreach (var mode in Enum.GetValues<RetrievalMode>())
		{
			foreach (var item in golden)
			{
				await using var scope = environment.Services.CreateAsyncScope();
				var retrieved = await scope.ServiceProvider.GetRequiredService<RetrieveContext>()
					.HandleAsync(tenant, item.Question, CancellationToken.None, mode);
				var rank = item.Answerable ? RankOfEvidence(retrieved.Chunks, item) : null;
				var first = retrieved.Chunks.FirstOrDefault();
				var topChunk = first is null ? null : $"{first.SourceUri} › {first.Heading}: {Preview(first.Text)}";
				results.Add(new ItemResult(item.Id, mode.ToString(), rank, retrieved.TopSimilarity, topChunk));
			}
		}

		var metrics = Enum.GetValues<RetrievalMode>()
			.Select(mode => Metrics(mode.ToString(), golden, results, languages))
			.ToList();
		var calibration = Calibrate(golden, results.Where(r => r.Mode == nameof(RetrievalMode.Hybrid)).ToList());

		return new RetrievalReport(DateTimeOffset.UtcNow, golden, metrics, calibration, results, languages);
	}

	/// <summary>A golden item that cites text missing from its document would silently score 0 forever.</summary>
	private static async Task ValidateGoldenAsync(EvalEnvironment environment, TenantId tenant, List<GoldenItem> golden)
	{
		await using var scope = environment.Services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
		var db = scope.ServiceProvider.GetRequiredService<DocsAssistantDbContext>();
		var chunks = await db.Chunks
			.Join(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { d.SourceUri, c.Heading, c.Text })
			.ToListAsync();

		var broken = golden
			.Where(g => g.Answerable)
			.Where(g => !chunks.Any(c => c.SourceUri == g.Doc && Contains(c.Heading, c.Text, g.Evidence!)))
			.Select(g => $"{g.Id}: '{g.Evidence}' not found in {g.Doc}")
			.ToList();
		if (broken.Count > 0)
		{
			throw new InvalidOperationException("Golden set references text that is not in the corpus:\n  " + string.Join("\n  ", broken));
		}
	}

	private static int? RankOfEvidence(IReadOnlyList<RetrievedChunk> chunks, GoldenItem item)
	{
		for (var i = 0; i < Math.Min(TopK, chunks.Count); i++)
		{
			if (chunks[i].SourceUri == item.Doc && Contains(chunks[i].Heading, chunks[i].Text, item.Evidence!))
			{
				return i + 1;
			}
		}

		return null;
	}

	private static string Preview(string text)
	{
		const int Length = 90;
		return text.Length <= Length ? text : text[..Length] + "…";
	}

	private static bool Contains(string? heading, string text, string evidence) =>
		Normalize($"{heading} {text}").Contains(Normalize(evidence), StringComparison.Ordinal);

	private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim().ToLowerInvariant();

	private static ModeMetrics Metrics(string mode, List<GoldenItem> golden, List<ItemResult> results, IReadOnlyDictionary<string, ContentLanguage> languages)
	{
		var answerable = golden.Where(g => g.Answerable).ToList();
		var ranks = answerable.Select(g => results.Single(r => r.Mode == mode && r.Id == g.Id).Rank).ToList();
		var cross = answerable
			.Where(g => IsCrossLingual(g, languages))
			.Select(g => results.Single(r => r.Mode == mode && r.Id == g.Id).Rank)
			.ToList();

		return new ModeMetrics(
			mode,
			answerable.Count,
			ranks.Count(r => r == 1) / (double)ranks.Count,
			ranks.Count(r => r is not null) / (double)ranks.Count,
			ranks.Sum(r => r is { } rank ? 1.0 / rank : 0) / ranks.Count,
			cross.Count,
			cross.Count == 0 ? 0 : cross.Count(r => r is not null) / (double)cross.Count);
	}

	internal static bool IsCrossLingual(GoldenItem item, IReadOnlyDictionary<string, ContentLanguage> languages)
	{
		var documentLanguage = languages[item.Doc!] == ContentLanguage.Spanish ? "es" : "en";
		return !documentLanguage.Equals(item.Lang, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Picks the similarity threshold that best separates answerable from unanswerable questions
	/// (maximum balanced accuracy, threshold placed midway between the two scores it falls between).
	/// </summary>
	private static Calibration Calibrate(List<GoldenItem> golden, List<ItemResult> hybrid)
	{
		var answerable = golden.Where(g => g.Answerable).Select(g => hybrid.Single(r => r.Id == g.Id).TopSimilarity).ToList();
		var unanswerable = golden.Where(g => !g.Answerable).Select(g => hybrid.Single(r => r.Id == g.Id).TopSimilarity).ToList();

		var scores = answerable.Concat(unanswerable).Distinct().Order().ToList();
		var bestThreshold = 0.0;
		var bestAccuracy = -1.0;
		for (var i = 0; i < scores.Count; i++)
		{
			var threshold = i == 0 ? scores[0] : (scores[i - 1] + scores[i]) / 2;
			var accuracy = (answerable.Count(s => s >= threshold) / (double)answerable.Count
				+ unanswerable.Count(s => s < threshold) / (double)unanswerable.Count) / 2;
			if (accuracy > bestAccuracy)
			{
				bestAccuracy = accuracy;
				bestThreshold = threshold;
			}
		}

		const double Step = 100.0;
		var floor = Math.Floor(answerable.Min() * Step) / Step;
		return new Calibration(
			floor,
			unanswerable.Count(s => s < floor),
			unanswerable.Count,
			Math.Round(bestThreshold, 3),
			bestAccuracy,
			answerable.Min(),
			unanswerable.Max(),
			answerable.Average(),
			unanswerable.Average());
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}

internal sealed record RetrievalReport(
	DateTimeOffset RanAt,
	IReadOnlyList<GoldenItem> Golden,
	IReadOnlyList<ModeMetrics> Modes,
	Calibration Calibration,
	IReadOnlyList<ItemResult> Results,
	IReadOnlyDictionary<string, ContentLanguage> Languages)
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	private ModeMetrics Hybrid => Modes.Single(m => m.Mode == nameof(RetrievalMode.Hybrid));

	public void Print()
	{
		Console.WriteLine();
		Console.WriteLine(Table());
		Console.WriteLine(CalibrationText());
		foreach (var miss in Misses())
		{
			Console.WriteLine($"  miss (hybrid): {miss}");
		}
	}

	public async Task WriteAsync(string directory)
	{
		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(
			Path.Combine(directory, "retrieval.json"),
			JsonSerializer.Serialize(new { RanAt, Modes, Calibration, Results }, Json));

		var markdown = new StringBuilder()
			.AppendLine("# Retrieval eval")
			.AppendLine()
			.AppendLine(CultureInfo.InvariantCulture, $"Run {RanAt:yyyy-MM-dd HH:mm} UTC · {Golden.Count(g => g.Answerable)} answerable + {Golden.Count(g => !g.Answerable)} unanswerable questions over {Languages.Count} documents (`evals/corpus`).")
			.AppendLine()
			.AppendLine(Table())
			.AppendLine()
			.AppendLine(CalibrationText())
			.AppendLine()
			.AppendLine("## Hybrid misses")
			.AppendLine();
		foreach (var miss in Misses())
		{
			markdown.AppendLine(CultureInfo.InvariantCulture, $"- {miss}");
		}

		await File.WriteAllTextAsync(Path.Combine(directory, "retrieval.md"), markdown.ToString());
	}

	public List<string> CheckThresholds(IReadOnlyDictionary<string, double> thresholds)
	{
		var actual = new Dictionary<string, double>
		{
			["hybrid.hitAt5"] = Hybrid.HitAt5,
			["hybrid.crossLingualHitAt5"] = Hybrid.CrossLingualHitAt5,
			["hybrid.mrr"] = Hybrid.Mrr,
		};
		return [.. thresholds
			.Where(t => !actual.TryGetValue(t.Key, out var value) || value < t.Value)
			.Select(t => $"{t.Key} = {actual.GetValueOrDefault(t.Key):F3} < {t.Value:F3}")];
	}

	private string Table()
	{
		var builder = new StringBuilder()
			.AppendLine("| Mode | hit@1 | hit@5 | MRR | cross-lingual hit@5 |")
			.AppendLine("|---|---:|---:|---:|---:|");
		foreach (var m in Modes)
		{
			builder.AppendLine(CultureInfo.InvariantCulture, $"| {m.Mode} | {m.HitAt1:P0} | {m.HitAt5:P0} | {m.Mrr:F3} | {m.CrossLingualHitAt5:P0} ({m.CrossLingual} q) |");
		}

		return builder.ToString().TrimEnd();
	}

	private string CalibrationText() => string.Create(
		CultureInfo.InvariantCulture,
		$"Abstention calibration (hybrid, best cosine similarity): answerable mean {Calibration.MeanAnswerable:F3} (min {Calibration.MinAnswerable:F3}), "
		+ $"unanswerable mean {Calibration.MeanUnanswerable:F3} (max {Calibration.MaxUnanswerable:F3}). "
		+ $"The ranges overlap, so similarity alone cannot decide: best single threshold {Calibration.BestSingleThreshold:F3} reaches only {Calibration.BestBalancedAccuracy:P0} balanced accuracy. "
		+ $"Safe floor (keeps every answerable question) Retrieval:MinSimilarity = {Calibration.SafeFloor:F2}, which alone abstains on {Calibration.UnanswerableBelowFloor}/{Calibration.Unanswerable} unanswerable questions; the rest is left to the model's grounded answer.");

	private IEnumerable<string> Misses() =>
		Golden.Where(g => g.Answerable)
			.Where(g => Results.Single(r => r.Mode == nameof(RetrievalMode.Hybrid) && r.Id == g.Id).Rank is null)
			.Select(g => $"{g.Id} — \"{g.Question}\" (expected {g.Doc}; got {Results.Single(r => r.Mode == nameof(RetrievalMode.Hybrid) && r.Id == g.Id).TopChunk})");
}
