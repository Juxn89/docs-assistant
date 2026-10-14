using System.ComponentModel.DataAnnotations;

namespace DocsAssistant.Application.Features.Handoff;

/// <summary>Bound from the "Handoff" configuration section and validated at startup.</summary>
public sealed class HandoffOptions
{
	public const string SectionName = "Handoff";

	/// <summary>The conversation shown in the operator's email: enough context to reply without asking again.</summary>
	[Range(0, 100)]
	public int TranscriptMessages { get; set; } = 20;

	/// <summary>A second identical request from the same visitor within this window is the same lead (double click, retry).</summary>
	[Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
	public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(10);

	/// <summary>Total sending attempts before a lead is marked Failed (it is kept and listed either way).</summary>
	[Range(1, 20)]
	public int MaxAttempts { get; set; } = 5;

	/// <summary>Delay before the first retry; it doubles on each further one, up to <see cref="MaxRetryDelay"/>.</summary>
	[Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
	public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);

	[Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
	public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(30);

	/// <summary>How often the worker looks for due leads (retries, and any the in-process queue lost).</summary>
	[Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
	public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

	[Range(1, 1000)]
	public int SweepBatchSize { get; set; } = 20;

	/// <summary>A Sending claim older than this belongs to a worker that died. An SMTP send takes seconds.</summary>
	[Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
	public TimeSpan SendingLease { get; set; } = TimeSpan.FromMinutes(5);
}
