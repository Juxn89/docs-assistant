namespace DocsAssistant.Infrastructure.Persistence;

/// <summary>Database-level names referenced from more than one place (configurations, raw SQL, tests).</summary>
internal static class PostgresNames
{
	public const string VectorExtension = "vector";

	public const string ChunksTable = "chunks";

	/// <summary>Shadow property mapped by Npgsql to the xmin system column (optimistic concurrency).</summary>
	public const string RowVersionProperty = "Version";

	public const string SearchVectorColumn = "search_vector";

	public const string EmbeddingColumn = "embedding";

	/// <summary>
	/// multilingual-e5-small produces 384 dimensions. Changing the embedding model means a migration
	/// that alters this column and re-embeds every chunk; the column type makes a mismatch fail loudly.
	/// </summary>
	public const int EmbeddingDimensions = 384;

	/// <summary>
	/// Postgres text-search configuration per <see cref="Domain.Common.ContentLanguage"/>, stored as its
	/// enum name. A literal CASE keeps the generated column immutable — a text→regconfig cast is not.
	/// </summary>
	public const string SearchVectorSql =
		"to_tsvector(CASE language WHEN 'Spanish' THEN 'spanish'::regconfig ELSE 'english'::regconfig END, "
		+ "coalesce(heading, '') || ' ' || text)";
}
