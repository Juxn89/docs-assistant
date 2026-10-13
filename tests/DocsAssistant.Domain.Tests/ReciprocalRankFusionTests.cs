using DocsAssistant.Domain.Retrieval;

namespace DocsAssistant.Domain.Tests;

public sealed class ReciprocalRankFusionTests
{
	[Fact]
	public void Fuse_ItemInBothRankings_BeatsItemTopInOnlyOne()
	{
		string[] vector = ["a", "b", "c"];
		string[] fullText = ["d", "b", "e"];

		var fused = ReciprocalRankFusion.Fuse([vector, fullText]);

		Assert.Equal("b", fused[0].Key);
	}

	[Fact]
	public void Fuse_UsesRanksNotMagnitudes()
	{
		// Same ranks, so the same fused order regardless of how different the underlying scores were.
		var fused = ReciprocalRankFusion.Fuse([(IReadOnlyList<int>)[1, 2], [2, 1]]);

		Assert.Equal(fused[0].Score, fused[1].Score, precision: 12);
	}

	[Fact]
	public void Fuse_TiesAreBrokenByFirstAppearance()
	{
		var fused = ReciprocalRankFusion.Fuse([(IReadOnlyList<string>)["x", "y"], ["y", "x"]]);

		Assert.Equal(["x", "y"], fused.Select(f => f.Key));
	}

	[Fact]
	public void Fuse_ScoreMatchesTheFormula()
	{
		var fused = ReciprocalRankFusion.Fuse([(IReadOnlyList<string>)["only"]], k: 60);

		Assert.Equal(1.0 / 61, Assert.Single(fused).Score, precision: 12);
	}

	[Fact]
	public void Fuse_EmptyRankings_ReturnsEmpty() =>
		Assert.Empty(ReciprocalRankFusion.Fuse(Array.Empty<IReadOnlyList<string>>()));
}
