using System.ComponentModel.DataAnnotations;

namespace DocsAssistant.Application.Features.Ingest;

/// <summary>Bound from the "Ingestion" configuration section and validated at startup.</summary>
public sealed class IngestionOptions
{
	public const string SectionName = "Ingestion";

	/// <summary>10 MB: comfortably above a long PDF manual, well below what fits in one request body.</summary>
	[Range(1, 50 * 1024 * 1024)]
	public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;

	/// <summary>A failing document is retried this many times in total before it is marked Failed.</summary>
	[Range(1, 10)]
	public int MaxAttempts { get; set; } = 3;

	/// <summary>How often the worker sweeps for Pending documents the queue lost (crash, deploy, retry).</summary>
	[Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
	public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

	[Range(1, 1000)]
	public int SweepBatchSize { get; set; } = 20;

	/// <summary>
	/// A Processing claim older than this belongs to a worker that died; the sweep releases it.
	/// Generous on purpose: a long PDF on a cold container can take minutes to embed.
	/// </summary>
	[Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
	public TimeSpan ProcessingLease { get; set; } = TimeSpan.FromMinutes(15);

	/// <summary>
	/// Passages handed to the embedder per call. Bounds memory and keeps progress visible; the ONNX
	/// adapter still infers them one at a time (see OnnxEmbedder for why).
	/// </summary>
	[Range(1, 256)]
	public int EmbeddingBatchSize { get; set; } = 16;
}
