using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Common;

namespace DocsAssistant.Application.Features.Ingest;

public sealed record UploadDocumentRequest(
	Guid TenantId,
	string FileName,
	string? MediaType,
	ReadOnlyMemory<byte> Content,
	string? Title);

public sealed class UploadDocument(RegisterDocument register)
{
	public Task<Result<RegistrationResponse>> HandleAsync(UploadDocumentRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		return register.HandleAsync(
			new TenantId(request.TenantId),
			request.Content,
			request.FileName,
			request.FileName,
			request.MediaType,
			request.Title,
			cancellationToken);
	}
}

public sealed record AddUrlDocumentRequest(Guid TenantId, string Url, string? Title);

/// <summary>
/// The page is fetched at registration, not in the worker: the content hash is the idempotency key,
/// and a URL that cannot be fetched should fail the request the admin is looking at, not a retry loop.
/// </summary>
public sealed class AddUrlDocument(IUrlFetcher fetcher, RegisterDocument register)
{
	public async Task<Result<RegistrationResponse>> HandleAsync(AddUrlDocumentRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url)
			|| (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
		{
			return Error.Validation("url.invalid", "Provide an absolute http(s) URL.");
		}

		var fetched = await fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);
		if (!fetched.IsSuccess)
		{
			return fetched.Error!;
		}

		var resource = fetched.Value;
		return await register.HandleAsync(
			new TenantId(request.TenantId),
			resource.Content,
			resource.FinalUri.ToString(),
			resource.FileName,
			resource.MediaType,
			request.Title,
			cancellationToken).ConfigureAwait(false);
	}
}

public sealed class ListDocuments(ITenantContext tenantContext, ITenantRepository tenants, IDocumentRepository documents)
{
	public async Task<Result<IReadOnlyList<DocumentResponse>>> HandleAsync(Guid tenantId, CancellationToken cancellationToken)
	{
		var id = new TenantId(tenantId);
		if (await tenants.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
		{
			return Error.NotFound("tenant.not_found", $"Tenant {tenantId} does not exist.");
		}

		tenantContext.Set(id);
		var all = await documents.ListAsync(cancellationToken).ConfigureAwait(false);
		return Result<IReadOnlyList<DocumentResponse>>.Success([.. all.Select(DocumentResponse.From)]);
	}
}
