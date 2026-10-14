using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;

namespace DocsAssistant.Application.Tests;

internal sealed class FakeTenantRepository : ITenantRepository
{
	public List<Tenant> Tenants { get; } = [];

	public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken) =>
		Task.FromResult(Tenants.SingleOrDefault(t => t.Id == id));

	public Task<Tenant?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken) =>
		Task.FromResult(Tenants.SingleOrDefault(t => t.PublicKey == publicKey));

	public void Add(Tenant tenant) => Tenants.Add(tenant);

	public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Honours the tenant context like the real query filter does, so isolation bugs show up here too.</summary>
internal sealed class FakeDocumentRepository(ITenantContext tenantContext) : IDocumentRepository
{
	private readonly Dictionary<DocumentId, byte[]> _contents = [];

	/// <summary>Last persisted state per document, so discarding behaves like EF's reload from the database.</summary>
	private readonly Dictionary<DocumentId, Dictionary<string, object?>> _persisted = [];

	public List<Document> Stored { get; } = [];

	public Dictionary<DocumentId, IReadOnlyList<Chunk>> Chunks { get; } = [];

	public int SaveCount { get; private set; }

	public int DiscardCount { get; private set; }

	/// <summary>When set, SaveIngestionAsync throws it once — simulates a failed final write.</summary>
	public Exception? FailIngestionWith { get; set; }

	private IEnumerable<Document> Visible => Stored.Where(d => d.TenantId == tenantContext.CurrentTenantId);

	public Task<Document?> GetAsync(DocumentId id, CancellationToken cancellationToken) =>
		Task.FromResult(Visible.SingleOrDefault(d => d.Id == id));

	public Task<Document?> FindByHashAsync(ContentHash hash, CancellationToken cancellationToken) =>
		Task.FromResult(Visible.SingleOrDefault(d => d.ContentHash == hash));

	public Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<Document>>([.. Visible]);

	public void Add(Document document, ReadOnlyMemory<byte> content)
	{
		Stored.Add(document);
		_contents[document.Id] = content.ToArray();
	}

	public Task<ReadOnlyMemory<byte>> ReadContentAsync(DocumentId id, CancellationToken cancellationToken) =>
		Task.FromResult<ReadOnlyMemory<byte>>(_contents[id]);

	public Task SaveIngestionAsync(Document document, IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken)
	{
		if (FailIngestionWith is { } failure)
		{
			FailIngestionWith = null;
			throw failure;
		}

		Chunks[document.Id] = chunks;
		return SaveChangesAsync(cancellationToken);
	}

	public Task DiscardUnsavedChangesAsync(Document document, CancellationToken cancellationToken)
	{
		DiscardCount++;
		foreach (var (name, value) in _persisted[document.Id])
		{
			typeof(Document).GetProperty(name)!.GetSetMethod(nonPublic: true)!.Invoke(document, [value]);
		}

		return Task.CompletedTask;
	}

	public Task SaveChangesAsync(CancellationToken cancellationToken)
	{
		SaveCount++;
		foreach (var document in Stored)
		{
			_persisted[document.Id] = typeof(Document).GetProperties()
				.Where(p => p.GetSetMethod(nonPublic: true) is not null)
				.ToDictionary(p => p.Name, p => p.GetValue(document));
		}

		return Task.CompletedTask;
	}
}

internal sealed class FakeQueue : IIngestionQueue
{
	public List<IngestionJob> Jobs { get; } = [];

	public ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken)
	{
		Jobs.Add(job);
		return ValueTask.CompletedTask;
	}
}

/// <summary>Deterministic 4-dimensional vectors; counts calls so batching can be asserted.</summary>
internal sealed class FakeEmbedder : IEmbedder
{
	public int Dimensions => 4;

	public List<int> BatchSizes { get; } = [];

	public Exception? FailWith { get; set; }

	public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken)
	{
		if (FailWith is not null)
		{
			throw FailWith;
		}

		BatchSizes.Add(passages.Count);
		return Task.FromResult<IReadOnlyList<float[]>>([.. passages.Select(p => new float[] { p.Length, 1, 0, 0 })]);
	}

	public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
		Task.FromResult(new float[] { query.Length, 1, 0, 0 });
}

internal sealed class FakeExtractor(DocumentSourceType format, Func<ReadOnlyMemory<byte>, ExtractedText> extract) : ITextExtractor
{
	public DocumentSourceType Format => format;

	public ExtractedText Extract(ReadOnlyMemory<byte> content) => extract(content);
}

internal sealed class FakeUrlFetcher(Result<FetchedResource> result) : IUrlFetcher
{
	public Task<Result<FetchedResource>> FetchAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(result);
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
	public override DateTimeOffset GetUtcNow() => now;
}
