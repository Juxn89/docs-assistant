var builder = DistributedApplication.CreateBuilder(args);

// pgvector ships as its own image; the tag is pinned so local runs, CI and evals see the same
// extension version (0.8.7) on the same Postgres major (18).
//
// Not WithDataVolume(): it mounts /var/lib/postgresql/data, and the Postgres 18 images moved PGDATA
// to a versioned subdirectory and refuse to start when a volume sits at the old path. The volume goes
// on /var/lib/postgresql instead, which is what the 18+ images expect.
var postgres = builder.AddPostgres("postgres")
	.WithImage("pgvector/pgvector")
	.WithImageTag("0.8.7-pg18")
	.WithVolume("docs-assistant-pgdata", "/var/lib/postgresql")
	.WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("docsdb");

var cache = builder.AddValkey("cache")
	.WithLifetime(ContainerLifetime.Persistent);

var mail = builder.AddMailPit("mail");

builder.AddProject<Projects.DocsAssistant_Api>("api")
	.WithReference(database)
	.WithReference(cache)
	.WithReference(mail)
	.WaitFor(database)
	.WaitFor(cache)
	.WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
