using System.Threading.Channels;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Infrastructure.Ingestion;

/// <summary>
/// In-process queue. It is a latency optimisation, not the source of truth: the database is. If the
/// process dies with jobs in this channel, the worker's sweep finds those documents still Pending.
/// That is why there is no broker here — at this scale a broker would add an outage mode without
/// adding a guarantee the database does not already give.
/// </summary>
internal sealed class ChannelIngestionQueue : IIngestionQueue
{
	/// <summary>Bounded so a burst of uploads applies back-pressure instead of growing memory without limit.</summary>
	private const int Capacity = 1000;

	private readonly Channel<IngestionJob> _channel = Channel.CreateBounded<IngestionJob>(
		new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

	public ChannelReader<IngestionJob> Reader => _channel.Reader;

	/// <summary>When full the job is dropped, not lost: the document is Pending and the sweep will find it.</summary>
	public ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken)
	{
		_channel.Writer.TryWrite(job);
		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// Processes one document at a time, each in its own DI scope (its own DbContext and tenant binding).
/// Jobs arrive from the channel immediately, and from a periodic sweep of Pending documents, which is
/// what makes retries and crash recovery work.
/// </summary>
internal sealed partial class IngestionWorker(
	ChannelIngestionQueue queue,
	IServiceScopeFactory scopes,
	IOptions<IngestionOptions> options,
	TimeProvider clock,
	ILogger<IngestionWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var sweep = SweepLoopAsync(stoppingToken);

		await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
		{
			await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
		}

		await sweep.ConfigureAwait(false);
	}

	private async Task SweepLoopAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
		do
		{
			try
			{
				await using var scope = scopes.CreateAsyncScope();
				var source = scope.ServiceProvider.GetRequiredService<IPendingDocumentSource>();
				await source.ReleaseExpiredClaimsAsync(options.Value.ProcessingLease, options.Value.MaxAttempts, stoppingToken)
					.ConfigureAwait(false);
				var pending = await source.ListPendingAsync(options.Value.SweepBatchSize, stoppingToken).ConfigureAwait(false);
				foreach (var job in pending)
				{
					await queue.EnqueueAsync(job, stoppingToken).ConfigureAwait(false);
				}
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// A sweep failing (database restarting) must not kill the worker; the next tick retries.
				LogSweepFailed(logger, exception);
			}
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	private async Task ProcessAsync(IngestionJob job, CancellationToken stoppingToken)
	{
		try
		{
			await using var scope = scopes.CreateAsyncScope();
			var process = scope.ServiceProvider.GetRequiredService<ProcessDocument>();
			await process.HandleAsync(job, stoppingToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// ProcessDocument records its own failures; reaching here means the failure could not even
			// be recorded. The document stays Pending/Processing and is not lost.
			LogJobCrashed(logger, exception, job.DocumentId.Value);
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Pending-document sweep failed; will retry on the next tick")]
	private static partial void LogSweepFailed(ILogger logger, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Ingestion of document {DocumentId} crashed outside the use case")]
	private static partial void LogJobCrashed(ILogger logger, Exception exception, Guid documentId);
}
