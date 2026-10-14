using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Features.Ingest;

public sealed record DocumentResponse(
	Guid Id,
	string Title,
	DocumentSourceType Format,
	string SourceUri,
	DocumentStatus Status,
	ContentLanguage Language,
	int ChunkCount,
	int Attempts,
	string? Error,
	DateTimeOffset CreatedAt,
	DateTimeOffset? ProcessedAt)
{
	public static DocumentResponse From(Document document)
	{
		ArgumentNullException.ThrowIfNull(document);
		return new(
			document.Id.Value,
			document.Title,
			document.SourceType,
			document.SourceUri,
			document.Status,
			document.Language,
			document.ChunkCount,
			document.Attempts,
			document.Error,
			document.CreatedAt,
			document.ProcessedAt);
	}
}

/// <summary>The outcome of a registration. <see cref="Duplicate"/> means nothing new was stored.</summary>
public sealed record RegistrationResponse(DocumentResponse Document, bool Duplicate);

/// <summary>
/// Shared core of every way a document enters the system (upload, URL): validate, deduplicate by
/// content hash, store document + bytes in one transaction, then hand it to the worker.
/// </summary>
public sealed class RegisterDocument(
	ITenantContext tenantContext,
	ITenantRepository tenants,
	IDocumentRepository documents,
	IIngestionQueue queue,
	IOptions<IngestionOptions> options,
	TimeProvider clock)
{
	private const int TitleMaxLength = 300;

	public async Task<Result<RegistrationResponse>> HandleAsync(
		TenantId tenantId,
		ReadOnlyMemory<byte> content,
		string sourceUri,
		string? fileName,
		string? mediaType,
		string? title,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);

		var tenant = await tenants.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
		if (tenant is null)
		{
			return Error.NotFound("tenant.not_found", $"Tenant {tenantId} does not exist.");
		}

		tenantContext.Set(tenantId);

		if (content.Length > options.Value.MaxFileBytes)
		{
			return Error.Validation(
				"document.too_large",
				$"The document is {content.Length:N0} bytes; the limit is {options.Value.MaxFileBytes:N0}.");
		}

		var format = FormatDetector.Detect(content.Span, fileName, mediaType);
		if (!format.IsSuccess)
		{
			return format.Error!;
		}

		var hash = ContentHash.Compute(content.Span);
		var existing = await documents.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);
		if (existing is not null)
		{
			return new RegistrationResponse(DocumentResponse.From(existing), Duplicate: true);
		}

		var document = Document.Register(
			tenantId,
			ResolveTitle(title, fileName, sourceUri),
			format.Value,
			sourceUri,
			hash,
			tenant.DefaultLanguage,
			clock.GetUtcNow());

		documents.Add(document, content);
		await documents.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await queue.EnqueueAsync(new IngestionJob(tenantId, document.Id), cancellationToken).ConfigureAwait(false);

		return new RegistrationResponse(DocumentResponse.From(document), Duplicate: false);
	}

	private static string ResolveTitle(string? title, string? fileName, string sourceUri)
	{
		var candidate = !string.IsNullOrWhiteSpace(title)
			? title
			: !string.IsNullOrWhiteSpace(fileName) ? Path.GetFileNameWithoutExtension(fileName) : sourceUri;
		candidate = candidate.Trim();
		return candidate.Length <= TitleMaxLength ? candidate : candidate[..TitleMaxLength];
	}
}
