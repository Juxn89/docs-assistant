using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Ingest;

public enum ProcessingOutcome
{
	Ready = 0,
	/// <summary>Failed this time; back to Pending, the sweep will pick it up again.</summary>
	WillRetry = 1,
	Failed = 2,
	/// <summary>Not ours to process: unknown id, or already taken by another run.</summary>
	Skipped = 3,
}

/// <summary>
/// Extract → detect language → chunk → embed → store, as one tenant-scoped unit of work.
/// Runs in the background worker; never in a request.
/// </summary>
public sealed partial class ProcessDocument(
	ITenantContext tenantContext,
	IDocumentRepository documents,
	IEnumerable<ITextExtractor> extractors,
	IEmbedder embedder,
	IOptions<IngestionOptions> options,
	TimeProvider clock,
	ILogger<ProcessDocument> logger)
{
	public async Task<ProcessingOutcome> HandleAsync(IngestionJob job, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(job);
		tenantContext.Set(job.TenantId);

		var document = await documents.GetAsync(job.DocumentId, cancellationToken).ConfigureAwait(false);
		if (document is null || document.Status != DocumentStatus.Pending)
		{
			return ProcessingOutcome.Skipped;
		}

		try
		{
			// Claiming is a write: if a second worker claimed it first, the concurrency token rejects this one.
			document.StartProcessing(clock.GetUtcNow());
			await documents.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (ConcurrencyConflictException)
		{
			return ProcessingOutcome.Skipped;
		}

		try
		{
			var chunks = await BuildChunksAsync(document, cancellationToken).ConfigureAwait(false);
			if (chunks.Count == 0)
			{
				document.MarkEmpty(clock.GetUtcNow());
				await documents.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
				LogEmpty(logger, document.Id.Value);
				return ProcessingOutcome.Failed;
			}

			document.MarkReady(chunks.Count, clock.GetUtcNow());
			await documents.SaveIngestionAsync(document, chunks, cancellationToken).ConfigureAwait(false);
			LogReady(logger, document.Id.Value, chunks.Count, document.Language);
			return ProcessingOutcome.Ready;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Whatever this attempt left in memory (chunks, a half-applied Ready) must not be saved
			// together with the failure.
			await documents.DiscardUnsavedChangesAsync(document, CancellationToken.None).ConfigureAwait(false);
			var willRetry = document.RecordFailure(Describe(exception), options.Value.MaxAttempts, clock.GetUtcNow());
			await documents.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
			LogFailed(logger, exception, document.Id.Value, document.Attempts, willRetry);
			return willRetry ? ProcessingOutcome.WillRetry : ProcessingOutcome.Failed;
		}
	}

	private async Task<List<Chunk>> BuildChunksAsync(Document document, CancellationToken cancellationToken)
	{
		var extractor = extractors.FirstOrDefault(e => e.Format == document.SourceType)
			?? throw new InvalidOperationException($"No extractor registered for {document.SourceType}.");

		var content = await documents.ReadContentAsync(document.Id, cancellationToken).ConfigureAwait(false);
		var extracted = extractor.Extract(content);

		document.SetLanguage(LanguageGuesser.Guess(extracted.PlainText, document.Language));

		var drafts = TextChunker.Chunk(extracted.Blocks, ChunkingOptions.Default);
		var chunks = new List<Chunk>(drafts.Count);
		foreach (var batch in drafts.Chunk(options.Value.EmbeddingBatchSize))
		{
			var embeddings = await embedder
				.EmbedPassagesAsync([.. batch.Select(PassageText)], cancellationToken)
				.ConfigureAwait(false);
			if (embeddings.Count != batch.Length)
			{
				throw new InvalidOperationException(
					$"The embedder returned {embeddings.Count} vectors for {batch.Length} passages.");
			}

			chunks.AddRange(batch.Select((draft, i) => Chunk.Create(document, draft, embeddings[i])));
		}

		return chunks;
	}

	/// <summary>The heading travels with the text into the embedding: "Returns › Refunds: …" retrieves better.</summary>
	private static string PassageText(ChunkDraft draft) =>
		draft.Heading is null ? draft.Text : $"{draft.Heading}: {draft.Text}";

	/// <summary>Stored on the document and shown to the admin; never a stack trace.</summary>
	private static string Describe(Exception exception) =>
		exception is DomainException or InvalidOperationException or FormatException
			? exception.Message
			: $"{exception.GetType().Name}: {exception.Message}";

	[LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} ready: {ChunkCount} chunks ({Language})")]
	private static partial void LogReady(ILogger logger, Guid documentId, int chunkCount, ContentLanguage language);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} has no extractable text")]
	private static partial void LogEmpty(ILogger logger, Guid documentId);

	[LoggerMessage(Level = LogLevel.Error, Message = "Document {DocumentId} failed on attempt {Attempt}; will retry: {WillRetry}")]
	private static partial void LogFailed(ILogger logger, Exception exception, Guid documentId, int attempt, bool willRetry);
}
