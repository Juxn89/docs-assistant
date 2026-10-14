using DocsAssistant.Domain.Common;

namespace DocsAssistant.Application.Abstractions;

/// <summary>A candidate from one retriever, ranked by that retriever's own (incomparable) score.</summary>
public sealed record ChunkHit(ChunkId ChunkId, double Score);

/// <summary>A chunk ready to be cited: its text, where it came from, and how close it is to the question.</summary>
public sealed record RetrievedChunk(
	ChunkId ChunkId,
	DocumentId DocumentId,
	string DocumentTitle,
	string SourceUri,
	int Ordinal,
	string? Heading,
	string Text,
	double Similarity);

/// <summary>
/// The two halves of hybrid search, tenant-scoped like every other read. Implementations must use the
/// bound tenant and fail closed without one.
/// </summary>
public interface IChunkSearch
{
	/// <summary>Nearest chunks by cosine similarity to <paramref name="queryEmbedding"/>.</summary>
	Task<IReadOnlyList<ChunkHit>> SearchByVectorAsync(float[] queryEmbedding, int limit, CancellationToken cancellationToken);

	/// <summary>Chunks matching any meaningful word of <paramref name="query"/>, in either supported language.</summary>
	Task<IReadOnlyList<ChunkHit>> SearchByTextAsync(string query, int limit, CancellationToken cancellationToken);

	/// <summary>Loads the chunks, with their similarity to the query, in no particular order.</summary>
	Task<IReadOnlyList<RetrievedChunk>> LoadAsync(IReadOnlyList<ChunkId> ids, float[] queryEmbedding, CancellationToken cancellationToken);
}
