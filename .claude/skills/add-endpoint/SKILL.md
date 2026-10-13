---
name: add-endpoint
description: Add a Minimal API endpoint backed by an Application use case, with validation and an integration test. Use when asked to add or expose an endpoint, route or API operation.
arguments: [feature, action]
---

Add the `$action` endpoint for the `$feature` feature.

## Existing features
!`ls src/DocsAssistant.Application/Features 2>/dev/null || echo "(none yet)"`

## Steps

1. Use case in `src/DocsAssistant.Application/Features/$feature/$action.cs`: a sealed class with
   `HandleAsync(request, CancellationToken)`, request/response as records. Business rules go in the
   Domain entity, not here.
2. Validate input at the boundary; return a typed result, map failures to ProblemDetails
   (400 validation, 404 not found, 409 domain conflict).
3. Endpoint in `src/DocsAssistant.Api/Endpoints/`, in the file named after the feature
   (e.g. `ChatEndpoints.cs`) — mapping only, no logic.
4. Register the use case in DI.
5. Integration test in `tests/DocsAssistant.Integration.Tests` covering the happy path and one failure,
   including tenant isolation if the data is tenant-owned.
6. Run `dotnet build` and `dotnet test`. Report the actual output.
