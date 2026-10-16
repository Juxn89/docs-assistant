using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace DocsAssistant.Evals;

/// <summary>USD per million tokens, from evals/pricing.json (with the date each price was checked).</summary>
internal sealed record ModelPrice(double InputPerMTok, double CacheReadPerMTok, double OutputPerMTok, string Verified)
{
	public static async Task<ModelPrice?> FindAsync(string pricingFile, string model)
	{
		await using var stream = File.OpenRead(pricingFile);
		using var document = await JsonDocument.ParseAsync(stream);
		return document.RootElement.TryGetProperty(model, out var price)
			? price.Deserialize<ModelPrice>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
			: null;
	}

	public double Cost(long input, long cachedInput, long output) =>
		((input - cachedInput) * InputPerMTok + cachedInput * CacheReadPerMTok + output * OutputPerMTok) / 1_000_000;
}

/// <summary>One model call as billed: tokens from the response's usage, or estimated in a dry run.</summary>
internal sealed record CallRecord(long InputTokens, long CachedInputTokens, long OutputTokens, string? FinishReason, double CostUsd, bool Estimated);

internal sealed class BudgetExceededException(string message) : Exception(message);

/// <summary>
/// Counts and prices every model call of a run, and refuses the next one before it is sent when it could
/// take the run past its call or cost limit. The cost check assumes the worst case for the call about to
/// be made: its estimated input plus the full output cap.
/// </summary>
internal sealed class Meter(ModelPrice price, int maxOutputTokens, int maxCalls, double maxCostUsd)
{
	// Character-based estimate, inflated for Haiku 5.5's tokenizer (about 30% more tokens than older models).
	private const double CharactersPerToken = 4.0;
	private const double TokenizerMargin = 1.3;

	private CallRecord? _last;

	public int Calls { get; private set; }

	public long InputTokens { get; private set; }

	public long CachedInputTokens { get; private set; }

	public long OutputTokens { get; private set; }

	public double CostUsd { get; private set; }

	public bool AnyEstimated { get; private set; }

	public ModelPrice Price => price;

	public static long EstimateTokens(IEnumerable<ChatMessage> messages, ChatOptions? options)
	{
		var characters = (options?.Instructions?.Length ?? 0) + messages.Sum(m => m.Text.Length);
		return (long)Math.Ceiling(characters / CharactersPerToken * TokenizerMargin);
	}

	public void EnsureCanCall(IEnumerable<ChatMessage> messages, ChatOptions? options)
	{
		if (Calls + 1 > maxCalls)
		{
			throw new BudgetExceededException($"call {Calls + 1} would exceed --max-calls {maxCalls}");
		}

		var worst = price.Cost(EstimateTokens(messages, options), 0, options?.MaxOutputTokens ?? maxOutputTokens);
		if (CostUsd + worst > maxCostUsd)
		{
			throw new BudgetExceededException(string.Create(
				CultureInfo.InvariantCulture,
				$"call {Calls + 1} could cost up to {worst:F6} USD; spent {CostUsd:F6} of the {maxCostUsd:F6} USD limit"));
		}
	}

	public void Record(CallRecord call)
	{
		Calls++;
		InputTokens += call.InputTokens;
		CachedInputTokens += call.CachedInputTokens;
		OutputTokens += call.OutputTokens;
		CostUsd += call.CostUsd;
		AnyEstimated |= call.Estimated;
		_last = call;
	}

	/// <summary>The call made since the last time this was asked, if any: one item makes at most one call.</summary>
	public CallRecord? TakeLast()
	{
		var last = _last;
		_last = null;
		return last;
	}
}

/// <summary>Meters a real model: checks the budget before the request and records the usage it reports.</summary>
internal sealed class MeteredChatClient(IChatClient inner, Meter meter) : DelegatingChatClient(inner)
{
	public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var list = messages.ToList();
		meter.EnsureCanCall(list, options);

		var usage = new UsageDetails();
		ChatFinishReason? finish = null;
		var stream = base.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
		var ended = false;
		try
		{
			while (true)
			{
				if (!await stream.MoveNextAsync().ConfigureAwait(false))
				{
					ended = true;
					break;
				}

				Observe(stream.Current);
				yield return stream.Current;
			}
		}
		finally
		{
			try
			{
				// The caller stops reading early when the model abstains (the NO_ANSWER sentinel), before
				// the usage arrives. Drain the rest, so the call is recorded with what it was billed.
				while (!ended && await stream.MoveNextAsync().ConfigureAwait(false))
				{
					Observe(stream.Current);
				}
			}
			finally
			{
				await stream.DisposeAsync().ConfigureAwait(false);

				// Recorded even when the stream failed: a request that reached the API may be billed.
				var input = usage.InputTokenCount ?? 0;
				var cached = usage.CachedInputTokenCount ?? 0;
				var output = usage.OutputTokenCount ?? 0;
				meter.Record(new CallRecord(input, cached, output, finish?.Value, meter.Price.Cost(input, cached, output), Estimated: false));
			}
		}

		void Observe(ChatResponseUpdate update)
		{
			foreach (var content in update.Contents.OfType<UsageContent>())
			{
				usage.Add(content.Details);
			}

			finish = update.FinishReason ?? finish;
		}
	}

	public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
}

/// <summary>
/// Stands in for the model in a dry run: answers every request with a cited sentence and reports an
/// estimated usage, so a run counts the exact calls it would make and what they would cost, for free.
/// </summary>
internal sealed class DryRunChatClient(Meter meter) : IChatClient
{
	private const string Answer = "Dry run: this would be the model's answer [1].";
	private const long EstimatedOutputTokens = 150;

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var list = messages.ToList();
		meter.EnsureCanCall(list, options);
		var input = Meter.EstimateTokens(list, options);
		meter.Record(new CallRecord(input, 0, EstimatedOutputTokens, ChatFinishReason.Stop.Value, meter.Price.Cost(input, 0, EstimatedOutputTokens), Estimated: true));

		await Task.Yield();
		yield return new ChatResponseUpdate(ChatRole.Assistant, Answer);
		yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = EstimatedOutputTokens })])
		{
			FinishReason = ChatFinishReason.Stop,
		};
	}

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}

/// <summary>
/// The budget ledger shared by every session that uses the same API account (a TSV outside the repo).
/// Read before a paid run to know what is left; one row appended after it with the real usage.
/// Append-only: rows are never rewritten.
/// </summary>
internal sealed record Ledger(string Path, string Session, double SessionCapUsd, double AccountCapUsd)
{
	private const int SessionColumn = 1;
	private const int UsdColumn = 10;

	public (double Session, double Total) Spent()
	{
		double session = 0, total = 0;
		foreach (var line in File.ReadLines(Path).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
		{
			var columns = line.Split('\t');
			var usd = double.Parse(columns[UsdColumn], CultureInfo.InvariantCulture);
			total += usd;
			if (columns[SessionColumn] == Session)
			{
				session += usd;
			}
		}

		return (session, total);
	}

	public double Remaining()
	{
		var (session, total) = Spent();
		return Math.Min(SessionCapUsd - session, AccountCapUsd - total);
	}

	public void Append(string project, string experiment, string model, Meter meter, string note)
	{
		var row = string.Join('\t',
			DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
			Session,
			project,
			experiment,
			model,
			meter.Calls.ToString(CultureInfo.InvariantCulture),
			(meter.InputTokens - meter.CachedInputTokens).ToString(CultureInfo.InvariantCulture),
			"0",
			meter.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
			meter.OutputTokens.ToString(CultureInfo.InvariantCulture),
			meter.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture),
			note);
		File.AppendAllText(Path, row + Environment.NewLine);
	}
}
