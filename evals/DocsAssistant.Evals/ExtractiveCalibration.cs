using System.Globalization;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace DocsAssistant.Evals;

/// <summary>
/// Measures whether the best <em>sentence</em> similarity separates answerable from unanswerable
/// questions better than the best <em>passage</em> similarity does (which overlaps, see the retrieval
/// eval). If it does, extractive mode can abstain on its own instead of quoting something irrelevant.
/// </summary>
internal static class ExtractiveCalibration
{
	public static async Task RunAsync(EvalEnvironment environment, RepoPaths repo)
	{
		Console.WriteLine("Ingesting corpus…");
		var (tenant, _) = await environment.IngestCorpusAsync(repo.CorpusDirectory);
		var golden = await GoldenSet.LoadAsync(repo);

		var answerable = new List<double>();
		var unanswerable = new List<double>();
		foreach (var item in golden)
		{
			await using var scope = environment.Services.CreateAsyncScope();
			var context = await scope.ServiceProvider.GetRequiredService<RetrieveContext>().HandleAsync(tenant, item.Question, CancellationToken.None);
			var sentences = await scope.ServiceProvider.GetRequiredService<ExtractiveAnswerer>().SelectAsync(item.Question, context.Chunks, CancellationToken.None);
			var best = sentences.Count == 0 ? 0 : sentences.Max(s => s.Score);
			(item.Answerable ? answerable : unanswerable).Add(best);
		}

		answerable.Sort();
		unanswerable.Sort();
		const double Step = 100.0;
		var floor = Math.Floor(answerable[0] * Step) / Step;
		Console.WriteLine(string.Create(
			CultureInfo.InvariantCulture,
			$"Best sentence similarity — answerable: min {answerable[0]:F3}, mean {answerable.Average():F3}; "
			+ $"unanswerable: max {unanswerable[^1]:F3}, mean {unanswerable.Average():F3}."));
		Console.WriteLine(string.Create(
			CultureInfo.InvariantCulture,
			$"Safe floor {floor:F2} keeps every answerable question and abstains on {unanswerable.Count(s => s < floor)}/{unanswerable.Count} unanswerable."));
		Console.WriteLine("answerable:   " + string.Join(' ', answerable.Select(s => s.ToString("F3", CultureInfo.InvariantCulture))));
		Console.WriteLine("unanswerable: " + string.Join(' ', unanswerable.Select(s => s.ToString("F3", CultureInfo.InvariantCulture))));
	}
}
