using System.Text.Json;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Documents;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace DocsAssistant.Infrastructure.Persistence;

internal sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value));

internal sealed class DocumentIdConverter() : ValueConverter<DocumentId, Guid>(id => id.Value, value => new DocumentId(value));

internal sealed class ChunkIdConverter() : ValueConverter<ChunkId, Guid>(id => id.Value, value => new ChunkId(value));

internal sealed class ConversationIdConverter() : ValueConverter<ConversationId, Guid>(id => id.Value, value => new ConversationId(value));

internal sealed class MessageIdConverter() : ValueConverter<MessageId, Guid>(id => id.Value, value => new MessageId(value));

internal sealed class LeadIdConverter() : ValueConverter<LeadId, Guid>(id => id.Value, value => new LeadId(value));

/// <summary>Citations are read and written with their message, never queried on their own: a jsonb column, not a table.</summary>
internal sealed class CitationsConverter()
	: ValueConverter<IReadOnlyList<MessageCitation>, string>(
		citations => JsonSerializer.Serialize(citations, CitationsJson.Options),
		json => JsonSerializer.Deserialize<List<MessageCitation>>(json, CitationsJson.Options) ?? new List<MessageCitation>());

internal sealed class CitationsComparer() : ValueComparer<IReadOnlyList<MessageCitation>>(
	(left, right) => left!.SequenceEqual(right!),
	citations => citations.Aggregate(0, (hash, citation) => HashCode.Combine(hash, citation)),
	citations => citations.ToList());

internal static class CitationsJson
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal sealed class ContentHashConverter()
	: ValueConverter<ContentHash, string>(hash => hash.Value, value => ContentHash.FromHex(value));

/// <summary>The domain keeps embeddings as plain float[]; only persistence knows about pgvector.</summary>
internal sealed class EmbeddingConverter()
	: ValueConverter<float[], Vector>(values => new Vector(values), vector => vector.ToArray());
