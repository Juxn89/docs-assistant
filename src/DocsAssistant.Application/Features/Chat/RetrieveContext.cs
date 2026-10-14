using System.ComponentModel.DataAnnotations;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Retrieval;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Chat;

public enum RetrievalMode
{
	Hybrid = 0,
	VectorOnly = 1,
	TextOnly = 2,
}

/// <summary>Bound from "Retrieval". The thresholds come from the evals, not from intuition.</summary>
public sealed class RetrievalOptions
{
	public const string SectionName = "Retrieval";

	/// <summary>Candidates each retriever contributes before fusion. Fusion needs depth to reward agreement.</summary>
	[Range(1, 200)]
	public int CandidatesPerRetriever { get; set; } = 20;

	/// <summary>Chunks handed to the model. Five is the hit@5 the evals measure.</summary>
	[Range(1, 20)]
	public int TopK { get; set; } = 5;

	public RetrievalMode Mode { get; set; } = RetrievalMode.Hybrid;

	/// <summary>RRF weight of the vector ranking.</summary>
	[Range(0.0, 10.0)]
	public double VectorWeight { get; set; } = 1.0;

	/// <summary>
	/// RRF weight of the full-text ranking. Measured on 2026-10-14 (evals/results/retrieval.md): 1.0
	/// gives the best hit@5 (93%) and cross-lingual hit@5 (86%); 0.3 trades 4 points of hit@5 for a
	/// better first rank. The model reads all top-K chunks, so recall wins.
	/// </summary>
	[Range(0.0, 10.0)]
	public double TextWeight { get; set; } = 1.0;

	/// <summary>
	/// Below this best cosine similarity the assistant abstains without calling the model. It is the
	/// eval's "safe floor": the lowest score any answerable question reached (0.792), rounded down, so it
	/// never refuses a question it could answer. Similarity alone cannot separate the rest — the
	/// answerable and unanswerable ranges overlap — so above the floor the grounded answer decides.
	/// </summary>
	[Range(0.0, 1.0)]
	public double MinSimilarity { get; set; } = 0.79;
}

/// <summary>What retrieval found, and whether it is confident enough to answer from.</summary>
public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, double TopSimilarity, bool IsConfident)
{
	public static readonly RetrievalResult Empty = new([], 0, false);
}

/// <summary>
/// Hybrid retrieval: vector search (meaning, across languages) and full-text search (exact terms,
/// product names, numbers) fused by Reciprocal Rank Fusion. Confidence is judged on cosine similarity,
/// which unlike the fused score has a stable meaning across questions.
/// </summary>
public sealed class RetrieveContext(
	ITenantContext tenantContext,
	IEmbedder embedder,
	IChunkSearch search,
	IOptions<RetrievalOptions> options)
{
	public async Task<RetrievalResult> HandleAsync(
		TenantId tenantId,
		string question,
		CancellationToken cancellationToken,
		RetrievalMode? modeOverride = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (string.IsNullOrWhiteSpace(question))
		{
			return RetrievalResult.Empty;
		}

		tenantContext.Set(tenantId);
		var settings = options.Value;
		var mode = modeOverride ?? settings.Mode;

		var embedding = await embedder.EmbedQueryAsync(question, cancellationToken).ConfigureAwait(false);

		// Sequential on purpose: both run on the scope's single database connection.
		var vector = mode == RetrievalMode.TextOnly
			? []
			: await search.SearchByVectorAsync(embedding, settings.CandidatesPerRetriever, cancellationToken).ConfigureAwait(false);
		var text = mode == RetrievalMode.VectorOnly
			? []
			: await search.SearchByTextAsync(question, settings.CandidatesPerRetriever, cancellationToken).ConfigureAwait(false);

		var ranked = ReciprocalRankFusion.Fuse(
		[
			((IReadOnlyList<ChunkId>)[.. vector.Select(h => h.ChunkId)], settings.VectorWeight),
			([.. text.Select(h => h.ChunkId)], settings.TextWeight),
		]);
		var top = ranked.Take(settings.TopK).Select(r => r.Key).ToList();
		if (top.Count == 0)
		{
			return RetrievalResult.Empty;
		}

		var loaded = (await search.LoadAsync(top, embedding, cancellationToken).ConfigureAwait(false))
			.ToDictionary(c => c.ChunkId);
		var chunks = top.Where(loaded.ContainsKey).Select(id => loaded[id]).ToList();
		var best = chunks.Count == 0 ? 0 : chunks.Max(c => c.Similarity);

		return new RetrievalResult(chunks, best, best >= settings.MinSimilarity);
	}
}
