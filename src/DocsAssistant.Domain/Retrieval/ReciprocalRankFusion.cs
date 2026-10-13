namespace DocsAssistant.Domain.Retrieval;

/// <summary>An item and its fused score. Higher is better.</summary>
public sealed record FusedResult<TKey>(TKey Key, double Score);

/// <summary>
/// Reciprocal Rank Fusion (Cormack, Clarke &amp; Büttcher, 2009): score(d) = Σ 1 / (k + rank(d)).
///
/// It fuses rankings, not scores. That is the whole point here: cosine similarity from pgvector and
/// ts_rank from full-text search live on unrelated scales, and adding or normalizing them rewards
/// whichever retriever happens to produce bigger numbers. Ranks are comparable; scores are not.
/// </summary>
public static class ReciprocalRankFusion
{
	/// <summary>The constant from the original paper; it damps the advantage of the very top ranks.</summary>
	public const int DefaultK = 60;

	public static IReadOnlyList<FusedResult<TKey>> Fuse<TKey>(
		IEnumerable<IReadOnlyList<TKey>> rankings,
		int k = DefaultK)
		where TKey : notnull
	{
		ArgumentNullException.ThrowIfNull(rankings);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

		var scores = new Dictionary<TKey, double>();
		var firstSeen = new Dictionary<TKey, int>();
		var order = 0;

		foreach (var ranking in rankings)
		{
			for (var index = 0; index < ranking.Count; index++)
			{
				var key = ranking[index];
				var rank = index + 1;
				scores[key] = scores.GetValueOrDefault(key) + (1.0 / (k + rank));
				firstSeen.TryAdd(key, order++);
			}
		}

		// Ties are broken by first appearance so the result is deterministic for a given input order.
		return
		[
			.. scores
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => firstSeen[pair.Key])
				.Select(pair => new FusedResult<TKey>(pair.Key, pair.Value)),
		];
	}
}
