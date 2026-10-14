using DocsAssistant.Domain.Common;

namespace DocsAssistant.Infrastructure.Persistence;

/// <summary>
/// The raw bytes of a document, kept in Postgres next to the document row. Persistence detail, not a
/// domain concept: it exists so ingestion can be retried without the original upload, and so the
/// document and its bytes commit in one transaction. At the 10 MB upload cap a bytea column is
/// simpler than a blob store and costs nothing extra on the free tier.
/// </summary>
internal sealed class DocumentContent
{
	public DocumentId DocumentId { get; init; }

	public TenantId TenantId { get; init; }

	public byte[] Bytes { get; init; } = [];
}
