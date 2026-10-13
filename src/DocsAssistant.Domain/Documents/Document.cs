using DocsAssistant.Domain.Common;

namespace DocsAssistant.Domain.Documents;

public enum DocumentSourceType
{
	Pdf = 0,
	Docx = 1,
	Markdown = 2,
	PlainText = 3,
	Url = 4,
}

public enum DocumentStatus
{
	Pending = 0,
	Processing = 1,
	Ready = 2,
	Failed = 3,
}

/// <summary>
/// A source the assistant answers from. Ingestion is a small state machine:
/// Pending → Processing → Ready, or back to Pending for a retry, or Failed once attempts run out.
/// Transitions outside that graph are bugs and throw.
/// </summary>
public sealed class Document
{
	private Document()
	{
		Title = string.Empty;
		SourceUri = string.Empty;
		ContentHash = null!;
	}

	public DocumentId Id { get; private set; }

	public TenantId TenantId { get; private set; }

	public string Title { get; private set; }

	public DocumentSourceType SourceType { get; private set; }

	/// <summary>The original file name, or the URL for web sources.</summary>
	public string SourceUri { get; private set; }

	public ContentHash ContentHash { get; private set; }

	public ContentLanguage Language { get; private set; }

	public DocumentStatus Status { get; private set; }

	public int Attempts { get; private set; }

	public string? Error { get; private set; }

	public int ChunkCount { get; private set; }

	public DateTimeOffset CreatedAt { get; private set; }

	public DateTimeOffset? ProcessedAt { get; private set; }

	public static Document Register(
		TenantId tenantId,
		string title,
		DocumentSourceType sourceType,
		string sourceUri,
		ContentHash contentHash,
		ContentLanguage language,
		DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceUri);
		ArgumentNullException.ThrowIfNull(contentHash);

		return new Document
		{
			Id = DocumentId.New(),
			TenantId = tenantId,
			Title = title.Trim(),
			SourceType = sourceType,
			SourceUri = sourceUri.Trim(),
			ContentHash = contentHash,
			Language = language,
			Status = DocumentStatus.Pending,
			CreatedAt = now,
		};
	}

	public void StartProcessing()
	{
		EnsureStatus(DocumentStatus.Pending, nameof(StartProcessing));
		Status = DocumentStatus.Processing;
		Attempts++;
		Error = null;
	}

	public void MarkReady(int chunkCount, DateTimeOffset now)
	{
		EnsureStatus(DocumentStatus.Processing, nameof(MarkReady));
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkCount);

		Status = DocumentStatus.Ready;
		ChunkCount = chunkCount;
		ProcessedAt = now;
	}

	/// <summary>
	/// Records a failed attempt. Returns <see langword="true"/> when the document went back to
	/// <see cref="DocumentStatus.Pending"/> for another attempt, <see langword="false"/> when it is
	/// now permanently <see cref="DocumentStatus.Failed"/>.
	/// </summary>
	public bool RecordFailure(string reason, int maxAttempts, DateTimeOffset now)
	{
		EnsureStatus(DocumentStatus.Processing, nameof(RecordFailure));
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

		Error = reason;
		if (Attempts < maxAttempts)
		{
			Status = DocumentStatus.Pending;
			return true;
		}

		Status = DocumentStatus.Failed;
		ProcessedAt = now;
		return false;
	}

	/// <summary>A document whose content produced no text at all is a permanent failure, not a retry.</summary>
	public void MarkEmpty(DateTimeOffset now)
	{
		EnsureStatus(DocumentStatus.Processing, nameof(MarkEmpty));
		Status = DocumentStatus.Failed;
		Error = "The document contains no extractable text.";
		ProcessedAt = now;
	}

	private void EnsureStatus(DocumentStatus expected, string operation)
	{
		if (Status != expected)
		{
			throw new DomainException(
				$"Cannot {operation} document {Id}: it is {Status}, expected {expected}.");
		}
	}
}
