using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;

namespace DocsAssistant.Application.Abstractions;

/// <summary>Text with its structure preserved, plus a title when the format carries one.</summary>
public sealed record ExtractedText(string? Title, IReadOnlyList<TextBlock> Blocks)
{
	public string PlainText => string.Join('\n', Blocks.Select(b => b.Text));
}

/// <summary>One implementation per format. Extraction must not touch the network or the database.</summary>
public interface ITextExtractor
{
	DocumentSourceType Format { get; }

	ExtractedText Extract(ReadOnlyMemory<byte> content);
}

/// <summary>
/// Embeddings for retrieval. Asymmetric on purpose: models like e5 expect queries and passages to be
/// marked differently, and that is a property of the model, which the adapter owns.
/// </summary>
public interface IEmbedder
{
	int Dimensions { get; }

	Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken);

	Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken);
}

public interface ITenantRepository
{
	Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken);

	Task<Tenant?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken);

	void Add(Tenant tenant);

	Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Tenant-scoped: every method sees only the documents of the bound tenant.</summary>
public interface IDocumentRepository
{
	Task<Document?> GetAsync(DocumentId id, CancellationToken cancellationToken);

	Task<Document?> FindByHashAsync(ContentHash hash, CancellationToken cancellationToken);

	Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken);

	/// <summary>Registers the document together with its raw bytes, committed in one transaction.</summary>
	void Add(Document document, ReadOnlyMemory<byte> content);

	Task<ReadOnlyMemory<byte>> ReadContentAsync(DocumentId id, CancellationToken cancellationToken);

	/// <summary>
	/// Atomically replaces every chunk of the document and saves the document's state, in one
	/// transaction: a document is never Ready with half its chunks, and a retry never duplicates them.
	/// </summary>
	Task SaveIngestionAsync(Document document, IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken);

	/// <summary>
	/// Drops every unsaved change and reloads the document's persisted state into the same instance.
	/// Used after a failed attempt, before recording the failure.
	/// </summary>
	Task DiscardUnsavedChangesAsync(Document document, CancellationToken cancellationToken);

	Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A unit of ingestion work. Carries the tenant so the worker can bind it before reading.</summary>
public sealed record IngestionJob(TenantId TenantId, DocumentId DocumentId);

public interface IIngestionQueue
{
	ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Recovery path for the queue: documents left Pending by a crash, a deploy, or a retry. This is the
/// one deliberately cross-tenant read in the system.
/// </summary>
public interface IPendingDocumentSource
{
	Task<IReadOnlyList<IngestionJob>> ListPendingAsync(int limit, CancellationToken cancellationToken);

	/// <summary>Releases Processing claims older than the lease (see Document.ReleaseExpiredClaim). Returns how many.</summary>
	Task<int> ReleaseExpiredClaimsAsync(TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);
}

public sealed record FetchedResource(Uri FinalUri, string? MediaType, ReadOnlyMemory<byte> Content, string? FileName);

/// <summary>
/// Fetches a public web resource. Implementations must refuse private, loopback and link-local
/// destinations (SSRF), cap size and time, and re-check every redirect.
/// </summary>
public interface IUrlFetcher
{
	Task<Result<FetchedResource>> FetchAsync(Uri url, CancellationToken cancellationToken);
}
