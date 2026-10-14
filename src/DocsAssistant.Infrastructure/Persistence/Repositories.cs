using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace DocsAssistant.Infrastructure.Persistence;

internal sealed class TenantRepository(DocsAssistantDbContext db) : ITenantRepository
{
	public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken) =>
		db.Tenants.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

	public Task<Tenant?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken) =>
		db.Tenants.SingleOrDefaultAsync(t => t.PublicKey == publicKey, cancellationToken);

	public void Add(Tenant tenant) => db.Tenants.Add(tenant);

	public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>Every query here goes through the tenant query filter; nothing calls IgnoreQueryFilters.</summary>
internal sealed class DocumentRepository(DocsAssistantDbContext db) : IDocumentRepository
{
	public Task<Document?> GetAsync(DocumentId id, CancellationToken cancellationToken) =>
		db.Documents.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

	public Task<Document?> FindByHashAsync(ContentHash hash, CancellationToken cancellationToken) =>
		db.Documents.SingleOrDefaultAsync(d => d.ContentHash == hash, cancellationToken);

	public async Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken) =>
		await db.Documents.AsNoTracking().OrderByDescending(d => d.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

	public void Add(Document document, ReadOnlyMemory<byte> content)
	{
		ArgumentNullException.ThrowIfNull(document);
		db.Documents.Add(document);
		db.DocumentContents.Add(new DocumentContent
		{
			DocumentId = document.Id,
			TenantId = document.TenantId,
			Bytes = content.ToArray(),
		});
	}

	public async Task<ReadOnlyMemory<byte>> ReadContentAsync(DocumentId id, CancellationToken cancellationToken)
	{
		var bytes = await db.DocumentContents
			.Where(c => c.DocumentId == id)
			.Select(c => c.Bytes)
			.SingleOrDefaultAsync(cancellationToken)
			.ConfigureAwait(false);
		return bytes ?? throw new InvalidOperationException($"Document {id} has no stored content.");
	}

	public async Task SaveIngestionAsync(Document document, IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(chunks);

		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async ct =>
		{
			await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
			await db.Chunks.Where(c => c.DocumentId == document.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			db.Chunks.AddRange(chunks);
			await SaveAsync(ct).ConfigureAwait(false);
			await transaction.CommitAsync(ct).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);
	}

	public async Task DiscardUnsavedChangesAsync(Document document, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(document);

		foreach (var entry in db.ChangeTracker.Entries().ToList())
		{
			if (!ReferenceEquals(entry.Entity, document))
			{
				entry.State = EntityState.Detached;
			}
		}

		await db.Entry(document).ReloadAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task SaveChangesAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

	private async Task SaveAsync(CancellationToken cancellationToken)
	{
		try
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException exception)
		{
			throw new ConcurrencyConflictException("The document was modified by another operation.", exception);
		}
	}
}

/// <summary>
/// The one deliberate cross-tenant read: the worker's recovery sweep. It returns ids only, and the
/// worker binds each job's tenant before touching the document.
/// </summary>
internal sealed class PendingDocumentSource(DocsAssistantDbContext db, TimeProvider clock) : IPendingDocumentSource
{
	public async Task<int> ReleaseExpiredClaimsAsync(TimeSpan lease, int maxAttempts, CancellationToken cancellationToken)
	{
		var now = clock.GetUtcNow();
		var cutoff = now - lease;
		var stale = await db.Documents
			.IgnoreQueryFilters()
			.Where(d => d.Status == DocumentStatus.Processing && d.ProcessingStartedAt < cutoff)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		var released = stale.Count(d => d.ReleaseExpiredClaim(now, lease, maxAttempts));
		try
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException)
		{
			// The "dead" worker finished after all. Its result wins; the next sweep re-evaluates.
			return 0;
		}

		return released;
	}

	public async Task<IReadOnlyList<IngestionJob>> ListPendingAsync(int limit, CancellationToken cancellationToken)
	{
		var pending = await db.Documents
			.IgnoreQueryFilters()
			.AsNoTracking()
			.Where(d => d.Status == DocumentStatus.Pending)
			.OrderBy(d => d.CreatedAt)
			.Take(limit)
			.Select(d => new { d.TenantId, d.Id })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		return [.. pending.Select(p => new IngestionJob(p.TenantId, p.Id))];
	}
}
