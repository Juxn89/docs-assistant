using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Domain.Common;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Tests;

public sealed class RetrieveContextTests
{
	private static readonly ChunkId A = ChunkId.New();
	private static readonly ChunkId B = ChunkId.New();
	private static readonly ChunkId C = ChunkId.New();

	private readonly FakeChunkSearch _search = new();
	private readonly RetrievalOptions _options = new() { TopK = 2, MinSimilarity = 0.8 };

	private RetrieveContext Retrieve() =>
		new(new TenantContext(), new FakeEmbedder(), _search, Options.Create(_options));

	[Fact]
	public async Task Hybrid_FusesBothRankings_AndKeepsFusedOrder()
	{
		_options.GuaranteedVectorHits = 0;
		_search.Vector = [A, B, C];
		_search.Text = [B, C];
		_search.Similarities = new() { [A] = 0.85, [B] = 0.9, [C] = 0.7 };

		var result = await Retrieve().HandleAsync(TenantId.New(), "q", CancellationToken.None);

		// B and C appear in both rankings and outscore A (only in one): B = 1/62 + 1/61, C = 1/63 + 1/62,
		// A = 1/61. Top-K cuts to two, and the order is the fused one even though LoadAsync returns them shuffled.
		Assert.Equal([B, C], result.Chunks.Select(c => c.ChunkId));
		Assert.Equal(0.9, result.TopSimilarity);
		Assert.True(result.IsConfident);
	}

	/// <summary>
	/// The cross-lingual case from the retrieval eval: the vector's first hit (A) is the answer, but the
	/// passages full-text search also returns outscore it in the fusion and push it out of the top K.
	/// </summary>
	[Fact]
	public async Task GuaranteedVectorHit_ReplacesTheLastFusedOne()
	{
		_options.GuaranteedVectorHits = 1;
		_search.Vector = [A, B, C];
		_search.Text = [B, C];
		_search.Similarities = new() { [A] = 0.85, [B] = 0.9, [C] = 0.7 };

		var result = await Retrieve().HandleAsync(TenantId.New(), "q", CancellationToken.None);

		Assert.Equal([B, A], result.Chunks.Select(c => c.ChunkId));
	}

	[Fact]
	public async Task GuaranteedVectorHit_AlreadyInTheTopK_ChangesNothing()
	{
		_options.GuaranteedVectorHits = 1;
		_search.Vector = [B, A, C];
		_search.Text = [B, C];
		_search.Similarities = new() { [A] = 0.85, [B] = 0.9, [C] = 0.7 };

		var result = await Retrieve().HandleAsync(TenantId.New(), "q", CancellationToken.None);

		Assert.Equal([B, C], result.Chunks.Select(c => c.ChunkId));
	}

	[Fact]
	public void OneVectorHit_IsGuaranteedByDefault() =>
		Assert.Equal(1, new RetrievalOptions().GuaranteedVectorHits);

	[Fact]
	public async Task BelowTheFloor_IsNotConfident()
	{
		_search.Vector = [A];
		_search.Similarities = new() { [A] = 0.79 };

		var result = await Retrieve().HandleAsync(TenantId.New(), "q", CancellationToken.None);

		Assert.False(result.IsConfident);
	}

	[Theory]
	[InlineData(RetrievalMode.VectorOnly, false, true)]
	[InlineData(RetrievalMode.TextOnly, true, false)]
	public async Task Modes_SkipTheOtherRetriever(RetrievalMode mode, bool expectVectorSkipped, bool expectTextSkipped)
	{
		_search.Vector = [A];
		_search.Text = [B];
		_search.Similarities = new() { [A] = 0.9, [B] = 0.9 };

		await Retrieve().HandleAsync(TenantId.New(), "q", CancellationToken.None, mode);

		Assert.Equal(expectVectorSkipped, !_search.VectorCalled);
		Assert.Equal(expectTextSkipped, !_search.TextCalled);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task BlankQuestion_RetrievesNothing(string question)
	{
		var result = await Retrieve().HandleAsync(TenantId.New(), question, CancellationToken.None);

		Assert.Same(RetrievalResult.Empty, result);
		Assert.False(_search.VectorCalled);
	}

	private sealed class FakeChunkSearch : IChunkSearch
	{
		public ChunkId[] Vector { get; set; } = [];

		public ChunkId[] Text { get; set; } = [];

		public Dictionary<ChunkId, double> Similarities { get; set; } = [];

		public bool VectorCalled { get; private set; }

		public bool TextCalled { get; private set; }

		public Task<IReadOnlyList<ChunkHit>> SearchByVectorAsync(float[] queryEmbedding, int limit, CancellationToken cancellationToken)
		{
			VectorCalled = true;
			return Task.FromResult<IReadOnlyList<ChunkHit>>([.. Vector.Select(id => new ChunkHit(id, Similarities[id]))]);
		}

		public Task<IReadOnlyList<ChunkHit>> SearchByTextAsync(string query, int limit, CancellationToken cancellationToken)
		{
			TextCalled = true;
			return Task.FromResult<IReadOnlyList<ChunkHit>>([.. Text.Select(id => new ChunkHit(id, 1))]);
		}

		public Task<IReadOnlyList<RetrievedChunk>> LoadAsync(IReadOnlyList<ChunkId> ids, float[] queryEmbedding, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<RetrievedChunk>>(
			[
				.. ids.Reverse().Select(id => new RetrievedChunk(id, DocumentId.New(), "doc", "doc.md", 0, null, "text", Similarities[id])),
			]);
	}
}
