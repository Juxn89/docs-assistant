using DocsAssistant.Infrastructure;
using DocsAssistant.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
	await DatabaseMigrator.MigrateAsync(app.Services);
}

app.MapDefaultEndpoints();

await app.RunAsync();
