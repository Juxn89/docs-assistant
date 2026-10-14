namespace DocsAssistant.Domain.Common;

/// <summary>
/// Strongly typed identifiers. Version 7 GUIDs are time-ordered, so they index well as primary keys
/// and a TenantId can never be passed where a DocumentId is expected.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
	public static TenantId New() => new(Guid.CreateVersion7());

	public override string ToString() => Value.ToString();
}

public readonly record struct DocumentId(Guid Value)
{
	public static DocumentId New() => new(Guid.CreateVersion7());

	public override string ToString() => Value.ToString();
}

public readonly record struct ChunkId(Guid Value)
{
	public static ChunkId New() => new(Guid.CreateVersion7());

	public override string ToString() => Value.ToString();
}

public readonly record struct ConversationId(Guid Value)
{
	public static ConversationId New() => new(Guid.CreateVersion7());

	public override string ToString() => Value.ToString();
}

public readonly record struct MessageId(Guid Value)
{
	public static MessageId New() => new(Guid.CreateVersion7());

	public override string ToString() => Value.ToString();
}

public readonly record struct LeadId(Guid Value)
{
	public static LeadId New() => new(Guid.CreateVersion7());

	public override string ToString() => Value.ToString();
}
