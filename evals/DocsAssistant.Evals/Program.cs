using DocsAssistant.Evals;

// Usage:
//   dotnet run --project evals/DocsAssistant.Evals                       retrieval eval (gates CI)
//   dotnet run --project evals/DocsAssistant.Evals -- --suite answers    end-to-end answers with the
//       configured provider, e.g. Ai__Provider=Ollama Ai__OllamaModel=qwen3:4b, or Ai__Provider=None
// Options: --connection "<postgres>" to reuse a database instead of starting a pgvector container.
var connection = ArgValue(args, "--connection") ?? Environment.GetEnvironmentVariable("EVALS_CONNECTION_STRING");
var suite = ArgValue(args, "--suite") ?? "retrieval";
var repo = RepoPaths.Locate();

await using var environment = await EvalEnvironment.StartAsync(connection);

if (suite == "extractive-calibration")
{
	await ExtractiveCalibration.RunAsync(environment, repo);
	return 0;
}

if (suite == "answers")
{
	var answers = await AnswerEval.RunAsync(environment, repo);
	answers.Print();
	await answers.WriteAsync(repo.ResultsDirectory);
	return 0;
}

var report = await RetrievalEval.RunAsync(environment, repo);
report.Print();
await report.WriteAsync(repo.ResultsDirectory);

var failures = report.CheckThresholds(await Thresholds.LoadAsync(repo.ThresholdsFile));
foreach (var failure in failures)
{
	Console.Error.WriteLine($"FAIL {failure}");
}

return failures.Count == 0 ? 0 : 1;

static string? ArgValue(string[] args, string name)
{
	var index = Array.IndexOf(args, name);
	return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
