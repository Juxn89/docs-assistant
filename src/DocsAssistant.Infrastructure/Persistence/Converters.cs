using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace DocsAssistant.Infrastructure.Persistence;

internal sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value));

internal sealed class DocumentIdConverter() : ValueConverter<DocumentId, Guid>(id => id.Value, value => new DocumentId(value));

internal sealed class ChunkIdConverter() : ValueConverter<ChunkId, Guid>(id => id.Value, value => new ChunkId(value));

internal sealed class ContentHashConverter()
	: ValueConverter<ContentHash, string>(hash => hash.Value, value => ContentHash.FromHex(value));

/// <summary>The domain keeps embeddings as plain float[]; only persistence knows about pgvector.</summary>
internal sealed class EmbeddingConverter()
	: ValueConverter<float[], Vector>(values => new Vector(values), vector => vector.ToArray());
