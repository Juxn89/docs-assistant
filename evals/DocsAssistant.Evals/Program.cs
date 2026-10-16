using System.Globalization;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Evals;
using DocsAssistant.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// Usage:
//   dotnet run --project evals/DocsAssistant.Evals                       retrieval eval (gates CI)
//   dotnet run --project evals/DocsAssistant.Evals -- --suite answers    end-to-end answers with the
//       configured provider, e.g. Ai__Provider=Ollama Ai__OllamaModel=qwen3:4b, or Ai__Provider=None
//   ... --suite answers|injection --dry-run   counts the model calls a run would make and prices them, free
// Options: --connection "<postgres>" to reuse a database instead of starting a pgvector container;
//   --only id1,id2 to run some items (results are written under a "subset" name).
//
// A suite that would bill a hosted model (Ai:Provider=Anthropic, the default) refuses to run without
// --allow-paid, --max-calls, --max-cost-usd and a budget ledger (--ledger, --ledger-session,
// --session-cap, --account-cap). The model call that could cross a limit is never sent; the extractive
// fallback and SDK retries are off, so the first error stops the run; the real usage is appended to the
// ledger when the run ends, also when it stops early.
const int Refused = 2;
const int Stopped = 3;

var connection = ArgValue(args, "--connection") ?? Environment.GetEnvironmentVariable("EVALS_CONNECTION_STRING");
var suite = ArgValue(args, "--suite") ?? "retrieval";
var dryRun = args.Contains("--dry-run");
var only = ArgValue(args, "--only") is { } ids ? new HashSet<string>(ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : null;
var repo = RepoPaths.Locate();

var requested = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var ai = new AiOptions();
requested.GetSection(AiOptions.SectionName).Bind(ai);
var usesModel = suite is "answers" or "injection";
var billed = usesModel && ai.Provider == ChatProvider.Anthropic && !dryRun;

var run = new RunContext(null, only, dryRun);
var settings = new Dictionary<string, string?>();
Func<IServiceProvider, IChatClient, IChatClient>? wrapChat = null;
Ledger? ledger = null;

if (billed || (usesModel && dryRun))
{
	var price = await ModelPrice.FindAsync(repo.PricingFile, ai.AnswerModel);
	if (price is null)
	{
		return Refuse($"{ai.AnswerModel} has no price in evals/pricing.json: a run that cannot be priced cannot be budgeted.");
	}

	// A dry run takes the same limits, to rehearse where a paid run would stop.
	var maxCalls = ArgDouble(args, "--max-calls") is { } dryCalls ? (int)dryCalls : int.MaxValue;
	var maxCost = ArgDouble(args, "--max-cost-usd") ?? double.MaxValue;
	if (billed)
	{
		if (!args.Contains("--allow-paid"))
		{
			return Refuse($"--suite {suite} would bill {ai.AnswerModel}. Use --dry-run to price it, or pass --allow-paid with the limits.");
		}

		if (ArgDouble(args, "--max-calls") is not { } calls || ArgDouble(args, "--max-cost-usd") is not { } cost
			|| ArgValue(args, "--ledger") is not { } ledgerPath || ArgValue(args, "--ledger-session") is not { } session
			|| ArgDouble(args, "--session-cap") is not { } sessionCap || ArgDouble(args, "--account-cap") is not { } accountCap)
		{
			return Refuse("a paid run needs --max-calls, --max-cost-usd, --ledger, --ledger-session, --session-cap and --account-cap.");
		}

		ledger = new Ledger(ledgerPath, session, sessionCap, accountCap);
		var (spentBySession, spentInTotal) = ledger.Spent();
		var remaining = ledger.Remaining();
		Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Ledger: {session} spent {spentBySession:F6} of {sessionCap:F2} USD; account {spentInTotal:F6} of {accountCap:F2} USD; {remaining:F6} USD left."));
		if (remaining <= 0)
		{
			return Refuse("the ledger has no budget left for this session.");
		}

		maxCalls = (int)calls;
		maxCost = Math.Min(cost, remaining);
		Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Paid run: {ai.AnswerModel}, at most {maxCalls} calls and {maxCost:F6} USD."));
	}

	var meter = new Meter(price, new ChatAnswerOptions().MaxOutputTokens, maxCalls, maxCost);
	run = run with { Meter = meter };
	settings["Chat:ExtractiveFallback"] = "false";
	settings["Ai:MaxRetries"] = "0";
	wrapChat = dryRun
		? (_, _) => new DryRunChatClient(meter)
		: (_, inner) => new MeteredChatClient(inner, meter);
}

await using var environment = await EvalEnvironment.StartAsync(connection, settings, wrapChat);

if (suite == "extractive-calibration")
{
	await ExtractiveCalibration.RunAsync(environment, repo);
	return 0;
}

if (usesModel)
{
	var exitCode = 0;
	var outcome = "completed";
	try
	{
		IReadOnlyList<string> failures;
		if (suite == "injection")
		{
			var injection = await InjectionEval.RunAsync(environment, repo, run);
			injection.Print();
			await injection.WriteAsync(repo.ResultsDirectory);
			failures = billed && only is null ? injection.CheckThresholds(await Thresholds.LoadAsync(repo.ThresholdsFile, "injection")) : [];
		}
		else
		{
			var answers = await AnswerEval.RunAsync(environment, repo, run);
			answers.Print();
			await answers.WriteAsync(repo.ResultsDirectory);
			failures = billed && only is null ? answers.CheckThresholds(await Thresholds.LoadAsync(repo.ThresholdsFile, "answers")) : [];
		}

		foreach (var failure in failures)
		{
			Console.Error.WriteLine($"FAIL {failure}");
		}

		exitCode = failures.Count == 0 ? 0 : 1;
	}
	catch (Exception exception) when (run.Meter is not null)
	{
		// Budget limit, API error or anything else: stop, keep the progress file, record what was spent.
		outcome = $"stopped: {exception.GetType().Name}: {exception.Message}";
		Console.Error.WriteLine();
		Console.Error.WriteLine($"STOPPED {exception.GetType().Name}: {exception.Message}");
		exitCode = Stopped;
	}
	finally
	{
		if (run.Meter is { } meter)
		{
			Console.WriteLine(string.Create(
				CultureInfo.InvariantCulture,
				$"Model calls: {meter.Calls} · tokens in {meter.InputTokens:N0} / out {meter.OutputTokens:N0} · {(meter.AnyEstimated ? "estimated " : string.Empty)}cost {meter.CostUsd:F6} USD"));
			if (ledger is not null && meter.Calls > 0)
			{
				var scope = only is null ? "full" : $"subset {string.Join(',', only)}";
				ledger.Append("docs-assistant", $"evals-{suite}", ai.AnswerModel, meter, $"{scope}; {outcome}");
				Console.WriteLine($"Ledger: row appended to {ledger.Path}.");
			}
		}
	}

	return exitCode;
}

var report = await RetrievalEval.RunAsync(environment, repo);
report.Print();
await report.WriteAsync(repo.ResultsDirectory);

var retrievalFailures = report.CheckThresholds(await Thresholds.LoadAsync(repo.ThresholdsFile));
foreach (var failure in retrievalFailures)
{
	Console.Error.WriteLine($"FAIL {failure}");
}

return retrievalFailures.Count == 0 ? 0 : 1;

static int Refuse(string reason)
{
	Console.Error.WriteLine($"REFUSED {reason}");
	return Refused;
}

static string? ArgValue(string[] args, string name)
{
	var index = Array.IndexOf(args, name);
	return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static double? ArgDouble(string[] args, string name) =>
	ArgValue(args, name) is { } value && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
