using DocsAssistant.Evals;

// Usage: dotnet run --project evals/DocsAssistant.Evals [-- --connection "<postgres>"]
// Without --connection a throwaway pgvector container is started (Docker required).
var connection = ArgValue(args, "--connection") ?? Environment.GetEnvironmentVariable("EVALS_CONNECTION_STRING");
var repo = RepoPaths.Locate();

await using var environment = await EvalEnvironment.StartAsync(connection);
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
