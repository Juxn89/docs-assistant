using DocsAssistant.Api.Admin;
using DocsAssistant.Application.Features.Handoff;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Application.Features.Tenants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Api.Endpoints;

/// <summary>Mapping only: parse the request, call the use case, translate the result.</summary>
internal static class AdminEndpoints
{
	/// <summary>Multipart framing (boundaries, headers, the title field) on top of the file itself.</summary>
	private const int MultipartOverheadBytes = 64 * 1024;

	/// <param name="maxFileBytes">The configured upload cap, so the server rejects oversized bodies before buffering them.</param>
	public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app, int maxFileBytes)
	{
		var admin = app.MapGroup("/v1/admin")
			.AddEndpointFilter<AdminKeyFilter>()
			.WithTags("Admin");

		admin.MapPost("/tenants", CreateTenantAsync);

		var documents = admin.MapGroup("/tenants/{tenantId:guid}/documents");
		documents.MapGet("/", ListDocumentsAsync);
		documents.MapPost("/", UploadAsync)
			// The admin API authenticates with a header, never a cookie, so there is no ambient
			// credential for a cross-site form to ride on: antiforgery tokens protect nothing here.
			.DisableAntiforgery()
			.WithMetadata(new RequestSizeLimitAttribute(maxFileBytes + MultipartOverheadBytes));
		documents.MapPost("/url", AddUrlAsync);

		admin.MapGet("/tenants/{tenantId:guid}/leads", ListLeadsAsync);

		return admin;
	}

	private static async Task<IResult> CreateTenantAsync(CreateTenantRequest request, CreateTenant useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(request, cancellationToken)).ToHttp(tenant => Results.Created($"/v1/admin/tenants/{tenant.Id}", tenant));

	private static async Task<IResult> ListDocumentsAsync(Guid tenantId, ListDocuments useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(tenantId, cancellationToken)).ToHttp(Results.Ok);

	private static async Task<IResult> UploadAsync(
		Guid tenantId,
		IFormFile file,
		[FromForm] string? title,
		UploadDocument useCase,
		IOptions<IngestionOptions> options,
		CancellationToken cancellationToken)
	{
		if (file.Length > options.Value.MaxFileBytes)
		{
			return Results.Problem(
				statusCode: StatusCodes.Status413PayloadTooLarge,
				title: $"The file exceeds {options.Value.MaxFileBytes:N0} bytes.");
		}

		using var buffer = new MemoryStream((int)file.Length);
		await file.CopyToAsync(buffer, cancellationToken);

		var result = await useCase.HandleAsync(
			new UploadDocumentRequest(tenantId, file.FileName, file.ContentType, buffer.ToArray(), title),
			cancellationToken);
		return result.ToHttp(Registered);
	}

	private static async Task<IResult> AddUrlAsync(Guid tenantId, AddUrlBody body, AddUrlDocument useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(new AddUrlDocumentRequest(tenantId, body.Url, body.Title), cancellationToken)).ToHttp(Registered);

	private static async Task<IResult> ListLeadsAsync(Guid tenantId, ListLeads useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(tenantId, cancellationToken)).ToHttp(Results.Ok);

	/// <summary>202 for new work the worker has not finished; 200 when the content was already known.</summary>
	private static IResult Registered(RegistrationResponse response) =>
		response.Duplicate ? Results.Ok(response) : Results.Accepted(value: response);

	internal sealed record AddUrlBody(string Url, string? Title);
}
