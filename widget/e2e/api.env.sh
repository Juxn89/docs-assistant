# Environment for the API under the end-to-end suite. Source it, then:
#   dotnet run --project src/DocsAssistant.Api --no-launch-profile
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://127.0.0.1:5010
export ConnectionStrings__docsdb='Host=127.0.0.1;Port=25432;Database=docsdb;Username=postgres;Password=e2e-only-not-a-secret'
export Admin__ApiKey=e2e-admin-key-not-a-secret-0123456789abcdef
export Ai__Provider=None
export Email__Host=127.0.0.1
export Email__Port=21025
export Email__Security=None
export Ingestion__SweepInterval=00:00:02
export Handoff__SweepInterval=00:00:02
