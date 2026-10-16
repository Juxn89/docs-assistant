using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Domain.Common;
using DocsAssistant.Infrastructure.Ai;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Evals;

internal sealed record AnswerItemResult(
	string Id,
	bool Answerable,
	string Outcome,
	bool CitedEvidence,
	bool HadInvalidCitations,
	double LatencyMs,
	string Answer,
	long? InputTokens = null,
	long? OutputTokens = null,
	string? FinishReason = null,
	double? CostUsd = null,
	bool? EvidenceRetrieved = null);

/// <param name="EvidenceRetrieved">Answerable questions whose evidence retrieval put in the passages the model reads.</param>
/// <param name="AnsweredWhenRetrievedRate">
/// Of those, the share answered: the model's own answer rate. AnsweredRate also counts the questions
/// retrieval failed, where abstaining is the right reply; retrieval recall is gated by its own eval.
/// </param>
internal sealed record AnswerMetrics(
	string Provider,
	int Answerable,
	double AnsweredRate,
	int EvidenceRetrieved,
	double AnsweredWhenRetrievedRate,
	double CitedEvidenceRate,
	int Unanswerable,
	double AbstentionRate,
	double InvalidCitationRate,
	double LatencyP50Ms,
	double LatencyP95Ms,
	int ModelCalls,
	long InputTokens,
	long OutputTokens,
	double CostUsd,
	int MaxTokensStops)
{
	/// <summary>Cost per thousand questions of this mix, including the ones answered without a model call.</summary>
	public double CostPer1000Questions(int questions) => questions == 0 ? 0 : CostUsd / questions * 1000;
}

/// <summary>
/// End-to-end answers through AskQuestion with whichever provider is configured (Ai:Provider). Scored
/// deterministically — no model grades another model: did it answer, did a citation point at the
/// passage holding the evidence, did it abstain when the corpus has no answer.
/// </summary>
internal static partial class AnswerEval
{
	public static async Task<AnswerReport> RunAsync(EvalEnvironment environment, RepoPaths repo, RunContext run)
	{
		Console.WriteLine("Ingesting corpus…");
		var (tenant, _) = await environment.IngestCorpusAsync(repo.CorpusDirectory);
		var golden = (await GoldenSet.LoadAsync(repo)).Where(g => run.Includes(g.Id)).ToList();
		var chunkText = await ChunkTextAsync(environment, tenant);
		var provider = run.Label(Describe(environment.Services.GetRequiredService<IOptions<AiOptions>>().Value));
		Console.WriteLine($"Answering {golden.Count} questions with {provider}…");

		// Checkpoint: every answered question is appended to a progress file as soon as it is scored, so
		// an interrupted run (a laptop out of memory, a closed terminal) resumes where it stopped instead
		// of repeating up to an hour of CPU inference.
		var progressFile = Path.Combine(repo.ResultsDirectory, $"answers-{AnswerReport.Slug(provider)}.progress.jsonl");
		var done = LoadProgress(progressFile);
		if (done.Count > 0)
		{
			Console.WriteLine($"Resuming: {done.Count} questions already answered in {Path.GetFileName(progressFile)}.");
		}

		Directory.CreateDirectory(repo.ResultsDirectory);
		await using (var progress = new StreamWriter(progressFile, append: true))
		{
			foreach (var item in golden.Where(g => !done.ContainsKey(g.Id)))
			{
				var result = await AnswerAsync(environment, tenant, item, chunkText, run.Meter);
				done[item.Id] = result;
				await progress.WriteLineAsync(JsonSerializer.Serialize(result));
				await progress.FlushAsync();
				Console.Write('.');
			}
		}

		var results = golden.Select(g => done[g.Id]).ToList();
		File.Delete(progressFile);
		Console.WriteLine();
		return new AnswerReport(DateTimeOffset.UtcNow, provider, Score(provider, results), results);
	}

	private static async Task<AnswerItemResult> AnswerAsync(
		EvalEnvironment environment,
		TenantId tenant,
		GoldenItem item,
		Dictionary<Guid, (string Doc, string Text)> chunkText,
		Meter? meter)
	{
		var retrieved = item.Answerable ? await EvidenceRetrievedAsync(environment, tenant, item, chunkText) : (bool?)null;

		await using var scope = environment.Services.CreateAsyncScope();
		var ask = scope.ServiceProvider.GetRequiredService<AskQuestion>();

		var clock = Stopwatch.StartNew();
		AnswerCompleted? completed = null;
		await foreach (var e in ask.HandleAsync(tenant, item.Question, ContentLanguage.English, CancellationToken.None))
		{
			completed = e as AnswerCompleted ?? completed;
		}

		clock.Stop();
		var cited = item.Answerable && completed!.Citations.Any(c =>
			chunkText.TryGetValue(c.ChunkId, out var source)
			&& source.Doc == item.Doc
			&& Normalize(source.Text).Contains(Normalize(item.Evidence!), StringComparison.Ordinal));
		var call = meter?.TakeLast();
		return new AnswerItemResult(
			item.Id, item.Answerable, completed!.Outcome.ToString(), cited, completed.HadInvalidCitations, clock.Elapsed.TotalMilliseconds, completed.Answer,
			call?.InputTokens, call?.OutputTokens, call?.FinishReason, call?.CostUsd, retrieved);
	}

	/// <summary>
	/// Whether the passages the model reads hold the evidence, by the same retrieval AskQuestion runs
	/// (deterministic, no model call). Separates "the model would not answer" from "it was not given the answer".
	/// </summary>
	private static async Task<bool> EvidenceRetrievedAsync(
		EvalEnvironment environment,
		TenantId tenant,
		GoldenItem item,
		Dictionary<Guid, (string Doc, string Text)> chunkText)
	{
		await using var scope = environment.Services.CreateAsyncScope();
		var context = await scope.ServiceProvider.GetRequiredService<RetrieveContext>().HandleAsync(tenant, item.Question, CancellationToken.None);
		return context.Chunks.Any(c =>
			chunkText.TryGetValue(c.ChunkId.Value, out var source)
			&& source.Doc == item.Doc
			&& Normalize(source.Text).Contains(Normalize(item.Evidence!), StringComparison.Ordinal));
	}

	private static Dictionary<string, AnswerItemResult> LoadProgress(string path) =>
		!File.Exists(path)
			? []
			: File.ReadAllLines(path)
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.Select(line => JsonSerializer.Deserialize<AnswerItemResult>(line)!)
				.ToDictionary(r => r.Id);

	private static AnswerMetrics Score(string provider, List<AnswerItemResult> results)
	{
		var answerable = results.Where(r => r.Answerable).ToList();
		var retrieved = answerable.Where(r => r.EvidenceRetrieved == true).ToList();
		var unanswerable = results.Where(r => !r.Answerable).ToList();
		var latencies = results.Select(r => r.LatencyMs).Order().ToList();

		return new AnswerMetrics(
			provider,
			answerable.Count,
			Rate(answerable.Count(r => IsAnswer(r.Outcome)), answerable.Count),
			retrieved.Count,
			Rate(retrieved.Count(r => IsAnswer(r.Outcome)), retrieved.Count),
			Rate(answerable.Count(r => r.CitedEvidence), answerable.Count),
			unanswerable.Count,
			Rate(unanswerable.Count(r => !IsAnswer(r.Outcome)), unanswerable.Count),
			Rate(results.Count(r => r.HadInvalidCitations), results.Count),
			Percentile(latencies, 0.50),
			Percentile(latencies, 0.95),
			results.Count(r => r.InputTokens is not null),
			results.Sum(r => r.InputTokens ?? 0),
			results.Sum(r => r.OutputTokens ?? 0),
			results.Sum(r => r.CostUsd ?? 0),
			results.Count(r => r.FinishReason == ChatFinishReason.Length.Value));
	}

	/// <summary>A subset can have no items of a kind (no unanswerable question): its rate is 0, not NaN.</summary>
	private static double Rate(int count, int total) => total == 0 ? 0 : count / (double)total;

	private static bool IsAnswer(string outcome) =>
		outcome is nameof(AnswerOutcome.Answered) or nameof(AnswerOutcome.AnsweredExtractively) or nameof(AnswerOutcome.AnsweredWithoutCitations);

	private static double Percentile(List<double> sorted, double p) =>
		sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

	private static string Describe(AiOptions ai) => ai.Provider switch
	{
		ChatProvider.Ollama => $"Ollama {ai.OllamaModel}",
		ChatProvider.Anthropic => $"Anthropic {ai.AnswerModel}",
		_ => "extractive (no model)",
	};

	private static async Task<Dictionary<Guid, (string Doc, string Text)>> ChunkTextAsync(EvalEnvironment environment, TenantId tenant)
	{
		await using var scope = environment.Services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
		var db = scope.ServiceProvider.GetRequiredService<DocsAssistantDbContext>();
		var rows = await db.Chunks
			.Join(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { c.Id, d.SourceUri, c.Heading, c.Text })
			.ToListAsync();
		return rows.ToDictionary(r => r.Id.Value, r => (r.SourceUri, $"{r.Heading} {r.Text}"));
	}

	private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim().ToLowerInvariant();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}

internal static class GoldenSet
{
	public static async Task<List<GoldenItem>> LoadAsync(RepoPaths repo) =>
		[
			.. (await File.ReadAllLinesAsync(Path.Combine(repo.DatasetsDirectory, "retrieval.jsonl")))
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.Select(line => JsonSerializer.Deserialize<GoldenItem>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!),
		];
}

internal sealed record AnswerReport(DateTimeOffset RanAt, string Provider, AnswerMetrics Metrics, IReadOnlyList<AnswerItemResult> Results)
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	/// <summary>The bar a language model has to clear on this set (evals/thresholds.json, "answers").</summary>
	public IReadOnlyList<string> CheckThresholds(IReadOnlyDictionary<string, double> thresholds)
	{
		var m = Metrics;
		var actual = new Dictionary<string, double>
		{
			["answeredRate"] = m.AnsweredRate,
			["answeredWhenRetrievedRate"] = m.AnsweredWhenRetrievedRate,
			["citedEvidenceRate"] = m.CitedEvidenceRate,
			["abstentionRate"] = m.AbstentionRate,
			["invalidCitationRate.max"] = m.InvalidCitationRate,
			["maxTokensStops.max"] = m.MaxTokensStops,
		};
		return ThresholdCheck.Failures(actual, thresholds);
	}

	public static string Slug(string provider) => Regex.Replace(provider.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

	public void Print()
	{
		Console.WriteLine($"Runtime: {CpuProfile.Describe()}");
		Console.WriteLine(Table());
	}

	public async Task WriteAsync(string directory)
	{
		Directory.CreateDirectory(directory);
		var slug = Slug(Provider);
		await File.WriteAllTextAsync(Path.Combine(directory, $"answers-{slug}.json"), JsonSerializer.Serialize(this, Json));

		var markdown = new StringBuilder()
			.AppendLine(CultureInfo.InvariantCulture, $"# Answer eval · {Provider}")
			.AppendLine()
			.AppendLine(CultureInfo.InvariantCulture, $"Run {RanAt:yyyy-MM-dd HH:mm} UTC · {CpuProfile.Describe()}")
			.AppendLine()
			.AppendLine(Table())
			.AppendLine()
			.AppendLine("## Answerable questions without the evidence cited")
			.AppendLine();
		foreach (var miss in Results.Where(r => r.Answerable && !r.CitedEvidence))
		{
			markdown.AppendLine(CultureInfo.InvariantCulture, $"- {miss.Id} ({miss.Outcome}): {Preview(miss.Answer)}");
		}

		markdown.AppendLine().AppendLine("## Unanswerable questions that got an answer").AppendLine();
		foreach (var leak in Results.Where(r => !r.Answerable && r.Outcome.StartsWith("Answered", StringComparison.Ordinal)))
		{
			markdown.AppendLine(CultureInfo.InvariantCulture, $"- {leak.Id} ({leak.Outcome}): {Preview(leak.Answer)}");
		}

		await File.WriteAllTextAsync(Path.Combine(directory, $"answers-{slug}.md"), markdown.ToString());
	}

	private string Table()
	{
		var m = Metrics;
		return string.Create(
			CultureInfo.InvariantCulture,
			$"""
			| Provider | answered | answered when the evidence was retrieved | cited the evidence | abstained (unanswerable) | invalid citations | latency p50 / p95 | model calls | tokens in / out | cost | per 1,000 questions |
			|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
			| {m.Provider} | {m.AnsweredRate:P0} ({m.Answerable} q) | {m.AnsweredWhenRetrievedRate:P0} ({m.EvidenceRetrieved} q) | {m.CitedEvidenceRate:P0} | {m.AbstentionRate:P0} ({m.Unanswerable} q) | {m.InvalidCitationRate:P0} | {m.LatencyP50Ms:N0} / {m.LatencyP95Ms:N0} ms | {m.ModelCalls} | {m.InputTokens:N0} / {m.OutputTokens:N0} | ${m.CostUsd:F4} | ${m.CostPer1000Questions(Results.Count):F2} |
			""");
	}

	private static string Preview(string text)
	{
		const int Length = 140;
		var flat = text.Replace('\n', ' ');
		return flat.Length <= Length ? flat : flat[..Length] + "…";
	}
}
