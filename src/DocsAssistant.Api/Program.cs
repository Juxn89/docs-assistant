using System.Text.Json.Serialization;
using DocsAssistant.Api.Admin;
using DocsAssistant.Api.Endpoints;
using DocsAssistant.Api.RateLimiting;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
	options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddOptions<AdminOptions>()
	.Bind(builder.Configuration.GetSection(AdminOptions.SectionName))
	.ValidateDataAnnotations()
	.ValidateOnStart();
builder.Services.AddSingleton<AdminKeyFilter>();
builder.Services.AddDocsAssistantRateLimiting(builder.Configuration);

var app = builder.Build();

// First: everything after it must see the real client address and be subject to the limits.
app.UseDocsAssistantRateLimiting();

// A malformed request (bad JSON, invalid UTF-8, wrong content type) is the client's error. Minimal
// APIs throw BadHttpRequestException for it in Development; without this the handler reports a 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
	StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
		? badRequest.StatusCode
		: StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
	await DatabaseMigrator.MigrateAsync(app.Services);
}

app.MapDefaultEndpoints();
app.MapChatEndpoints();
app.MapAdminEndpoints(app.Services.GetRequiredService<IOptions<IngestionOptions>>().Value.MaxFileBytes);

await app.RunAsync();
