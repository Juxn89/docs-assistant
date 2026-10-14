using System.Text.Json.Serialization;
using DocsAssistant.Api.Admin;
using DocsAssistant.Api.Endpoints;
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

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
	await DatabaseMigrator.MigrateAsync(app.Services);
}

app.MapDefaultEndpoints();
app.MapAdminEndpoints(app.Services.GetRequiredService<IOptions<IngestionOptions>>().Value.MaxFileBytes);

await app.RunAsync();
