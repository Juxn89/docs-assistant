using System.Threading.Channels;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Handoff;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Infrastructure.Handoff;

/// <summary>
/// In-process queue, a latency optimisation like the ingestion one: the leads table is the source of
/// truth, and the sweep finds whatever this channel drops or loses in a restart.
/// </summary>
internal sealed class ChannelLeadQueue : ILeadQueue
{
	private const int Capacity = 1000;

	private readonly Channel<LeadJob> _channel = Channel.CreateBounded<LeadJob>(
		new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

	public ChannelReader<LeadJob> Reader => _channel.Reader;

	public ValueTask EnqueueAsync(LeadJob job, CancellationToken cancellationToken)
	{
		_channel.Writer.TryWrite(job);
		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// Sends lead notifications one at a time, each in its own DI scope. New leads arrive through the
/// channel; retries (due later) and anything the channel lost arrive through the periodic sweep.
/// Idle, with a warning, when no SMTP server is configured: leads still accumulate and are listed.
/// </summary>
internal sealed partial class LeadNotificationWorker(
	ChannelLeadQueue queue,
	IServiceScopeFactory scopes,
	IOptions<HandoffOptions> options,
	IOptions<EmailOptions> email,
	TimeProvider clock,
	ILogger<LeadNotificationWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!email.Value.IsConfigured)
		{
			LogNoSmtp(logger);
			return;
		}

		var sweep = SweepLoopAsync(stoppingToken);

		await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
		{
			await NotifyAsync(job, stoppingToken).ConfigureAwait(false);
		}

		await sweep.ConfigureAwait(false);
	}

	private async Task SweepLoopAsync(CancellationToken stoppingToken)
	{
		var settings = options.Value;
		using var timer = new PeriodicTimer(settings.SweepInterval, clock);
		do
		{
			try
			{
				await using var scope = scopes.CreateAsyncScope();
				var source = scope.ServiceProvider.GetRequiredService<IPendingLeadSource>();
				await source.ReleaseExpiredClaimsAsync(settings.SendingLease, settings.MaxAttempts, stoppingToken).ConfigureAwait(false);
				foreach (var job in await source.ListDueAsync(clock.GetUtcNow(), settings.SweepBatchSize, stoppingToken).ConfigureAwait(false))
				{
					await queue.EnqueueAsync(job, stoppingToken).ConfigureAwait(false);
				}
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				LogSweepFailed(logger, exception);
			}
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	private async Task NotifyAsync(LeadJob job, CancellationToken stoppingToken)
	{
		try
		{
			await using var scope = scopes.CreateAsyncScope();
			await scope.ServiceProvider.GetRequiredService<NotifyLead>().HandleAsync(job, stoppingToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// NotifyLead records its own failures; reaching here means even that failed. The lead stays
			// Pending or Sending and the sweep comes back to it.
			LogJobCrashed(logger, exception, job.LeadId.Value);
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "No SMTP server configured (Email:Host): leads are stored but nobody is notified")]
	private static partial void LogNoSmtp(ILogger logger);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Due-lead sweep failed; will retry on the next tick")]
	private static partial void LogSweepFailed(ILogger logger, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Notification of lead {LeadId} crashed outside the use case")]
	private static partial void LogJobCrashed(ILogger logger, Exception exception, Guid leadId);
}
