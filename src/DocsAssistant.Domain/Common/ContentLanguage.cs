namespace DocsAssistant.Domain.Common;

/// <summary>
/// Languages the full-text side of the hybrid search knows how to stem. The vector side is
/// multilingual and does not care; this only selects the Postgres text-search configuration.
/// </summary>
public enum ContentLanguage
{
	English = 0,
	Spanish = 1,
}
