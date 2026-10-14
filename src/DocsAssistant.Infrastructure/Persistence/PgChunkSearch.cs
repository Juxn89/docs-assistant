using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace DocsAssistant.Infrastructure.Persistence;

/// <summary>
/// Raw SQL on purpose: these are the two queries the product's quality depends on, and they use
/// pgvector and full-text operators EF Core cannot express. Raw SQL bypasses EF's query filters, so
/// every statement here filters by tenant explicitly and refuses to run without one.
/// </summary>
internal sealed class PgChunkSearch(DocsAssistantDbContext db, ITenantContext tenantContext) : IChunkSearch
{
	/// <summary>
	/// Approximate search. When the planner uses the HNSW index with the tenant filter applied
	/// afterwards (it does when one tenant dominates the table), the scan can come back short.
	/// pgvector 0.8's iterative scan (strict_order) keeps walking the graph to fill the limit, which
	/// fixes the common case. See <see cref="ExactVectorSql"/> for the case it does not fix.
	/// </summary>
	private const string VectorSql = """
		SET LOCAL hnsw.iterative_scan = strict_order;
		SELECT id, 1 - (embedding <=> @query) AS similarity
		FROM chunks
		WHERE tenant_id = @tenant
		ORDER BY embedding <=> @query
		LIMIT @limit;
		""";

	/// <summary>
	/// Exact search, used only when <see cref="VectorSql"/> returned fewer rows than the tenant has.
	/// Measured on 2026-10-14 (pgvector 0.8.7): with the tenant's vectors in a region of the HNSW graph
	/// the scan never reaches, even strict_order returns zero rows while an exact scan returns the full
	/// limit. Turning index scans off for this one statement makes Postgres read the tenant's rows
	/// (bitmap scan on tenant_id, or sequential) and sort them exactly. It costs a scan of one
	/// tenant's chunks, and only on the rare query that needed it.
	/// </summary>
	private const string ExactVectorSql = """
		SET LOCAL enable_indexscan = off;
		SELECT id, 1 - (embedding <=> @query) AS similarity
		FROM chunks
		WHERE tenant_id = @tenant
		ORDER BY embedding <=> @query
		LIMIT @limit;
		""";

	private const string TenantChunkCountSql = "SELECT count(*) FROM chunks WHERE tenant_id = @tenant;";

	/// <summary>
	/// Questions are phrased naturally, so AND-ing every word (plainto/websearch_to_tsquery) almost never
	/// matches. The query is stemmed per language, its terms OR-ed, and both languages combined; stop
	/// words are dropped by each configuration. ts_rank_cd then prefers chunks covering more terms.
	/// Ties are broken by document and position, never by chunk id: version 7 GUIDs created in the same
	/// millisecond are randomly ordered, which made identical runs rank differently.
	/// </summary>
	private const string TextSql = """
		WITH q AS (
			SELECT replace(plainto_tsquery('spanish', @text)::text, ' & ', ' | ')::tsquery
				|| replace(plainto_tsquery('english', @text)::text, ' & ', ' | ')::tsquery AS query
		)
		SELECT c.id, ts_rank_cd(c.search_vector, q.query, 32) AS rank
		FROM chunks c, q
		WHERE c.tenant_id = @tenant AND c.search_vector @@ q.query
		ORDER BY rank DESC, c.document_id, c.ordinal
		LIMIT @limit;
		""";

	private const string LoadSql = """
		SELECT c.id, c.document_id, d.title, d.source_uri, c.ordinal, c.heading, c.text,
			1 - (c.embedding <=> @query) AS similarity
		FROM chunks c
		JOIN documents d ON d.id = c.document_id
		WHERE c.tenant_id = @tenant AND c.id = ANY(@ids);
		""";

	public async Task<IReadOnlyList<ChunkHit>> SearchByVectorAsync(float[] queryEmbedding, int limit, CancellationToken cancellationToken)
	{
		void AddParameters(NpgsqlCommand command)
		{
			command.Parameters.Add(new NpgsqlParameter("query", new Vector(queryEmbedding)));
			command.Parameters.Add(new NpgsqlParameter("limit", limit));
		}

		var hits = await QueryHitsAsync(VectorSql, AddParameters, inTransaction: true, cancellationToken).ConfigureAwait(false);
		if (hits.Count >= limit || hits.Count >= await CountTenantChunksAsync(cancellationToken).ConfigureAwait(false))
		{
			return hits;
		}

		return await QueryHitsAsync(ExactVectorSql, AddParameters, inTransaction: true, cancellationToken).ConfigureAwait(false);
	}

	public Task<IReadOnlyList<ChunkHit>> SearchByTextAsync(string query, int limit, CancellationToken cancellationToken) =>
		QueryHitsAsync(TextSql, command =>
		{
			command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = query });
			command.Parameters.Add(new NpgsqlParameter("limit", limit));
		}, inTransaction: false, cancellationToken);

	public async Task<IReadOnlyList<RetrievedChunk>> LoadAsync(
		IReadOnlyList<ChunkId> ids,
		float[] queryEmbedding,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(ids);
		if (ids.Count == 0)
		{
			return [];
		}

		var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = CreateCommand(connection, LoadSql);
		command.Parameters.Add(new NpgsqlParameter("query", new Vector(queryEmbedding)));
		command.Parameters.Add(new NpgsqlParameter("ids", ids.Select(id => id.Value).ToArray()));

		var chunks = new List<RetrievedChunk>(ids.Count);
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			chunks.Add(new RetrievedChunk(
				new ChunkId(reader.GetGuid(0)),
				new DocumentId(reader.GetGuid(1)),
				reader.GetString(2),
				reader.GetString(3),
				reader.GetInt32(4),
				await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
				reader.GetString(6),
				reader.GetDouble(7)));
		}

		return chunks;
	}

	private async Task<long> CountTenantChunksAsync(CancellationToken cancellationToken)
	{
		var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = CreateCommand(connection, TenantChunkCountSql);
		return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
	}

	private async Task<IReadOnlyList<ChunkHit>> QueryHitsAsync(
		string sql,
		Action<NpgsqlCommand> addParameters,
		bool inTransaction,
		CancellationToken cancellationToken)
	{
		var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

		// SET LOCAL only lives inside a transaction; without one it would silently do nothing.
		await using var transaction = inTransaction
			? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
			: null;
		await using var command = CreateCommand(connection, sql);
		command.Transaction = transaction;
		addParameters(command);

		var hits = new List<ChunkHit>();
		await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				hits.Add(new ChunkHit(new ChunkId(reader.GetGuid(0)), Convert.ToDouble(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
			}
		}

		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}

		return hits;
	}

	private NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql)
	{
		var tenant = tenantContext.CurrentTenantId
			?? throw new InvalidOperationException("Chunk search requires a bound tenant; refusing to search across tenants.");
		var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.Add(new NpgsqlParameter("tenant", tenant.Value));
		return command;
	}

	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		return (NpgsqlConnection)db.Database.GetDbConnection();
	}
}
