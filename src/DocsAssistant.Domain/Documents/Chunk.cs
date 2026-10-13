using DocsAssistant.Domain.Common;

namespace DocsAssistant.Domain.Documents;

/// <summary>
/// A retrievable slice of a document. <see cref="TenantId"/> is denormalized on purpose: every
/// retrieval query filters by tenant first, and doing that without a join keeps the HNSW and GIN
/// indexes usable.
/// </summary>
public sealed class Chunk
{
	private Chunk()
	{
		Text = string.Empty;
		Embedding = [];
	}

	public ChunkId Id { get; private set; }

	public DocumentId DocumentId { get; private set; }

	public TenantId TenantId { get; private set; }

	/// <summary>Position inside the document, starting at 0. Used to cite and to show neighbours.</summary>
	public int Ordinal { get; private set; }

	/// <summary>Heading path the chunk lives under, e.g. "Experience › Stepstone". Null for flat text.</summary>
	public string? Heading { get; private set; }

	public string Text { get; private set; }

	public ContentLanguage Language { get; private set; }

	public float[] Embedding { get; private set; }

	public static Chunk Create(
		Document document,
		ChunkDraft draft,
		float[] embedding)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(draft);
		ArgumentNullException.ThrowIfNull(embedding);
		if (embedding.Length == 0)
		{
			throw new DomainException("A chunk cannot be stored without its embedding.");
		}

		return new Chunk
		{
			Id = ChunkId.New(),
			DocumentId = document.Id,
			TenantId = document.TenantId,
			Ordinal = draft.Ordinal,
			Heading = draft.Heading,
			Text = draft.Text,
			Language = document.Language,
			Embedding = embedding,
		};
	}
}
