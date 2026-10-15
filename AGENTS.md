# docs-assistant · Agent guidelines

Embeddable AI support assistant: answers from a tenant's own documents with citations, abstains
when it does not know, and hands off to a human. Hybrid RAG over Postgres (pgvector + full-text).

These rules are binding for any agent (Claude Code, Codex, Copilot) working in this repo.

<!-- BEGIN:managed-rules -->

## Stack (pinned — check `Directory.Packages.props` before adding anything)

- .NET 10 / C# 14 (`global.json`), Central Package Management, `TreatWarningsAsErrors=true`.
- ASP.NET Core Minimal APIs, ProblemDetails. Aspire 13.6 AppHost for local dev.
- EF Core 10 + Npgsql + pgvector (`pgvector/pgvector:0.8.7-pg18`), snake_case naming.
- AI: `Microsoft.Extensions.AI` abstractions as ports; Claude via the official `Anthropic` SDK;
  embeddings in-process with ONNX (`multilingual-e5-small`, 384 dims).
- Tests: xUnit **v2** (ArchUnitNET's adapter requires it — do not move to xunit.v3), Testcontainers.

## Dependency rule (enforced by `tests/DocsAssistant.Architecture.Tests`)

```
Domain  ←  Application  ←  Infrastructure  ←  Api
```

- `Domain`: plain C#. No package references. Entities guard their own invariants.
- `Application`: use cases organized by feature (`Features/<Feature>/<UseCase>.cs`) plus ports
  (`Abstractions/`). May reference `Microsoft.Extensions.AI.Abstractions`, nothing concrete.
- `Infrastructure`: adapters (EF Core, Anthropic, ONNX, Valkey, SMTP).
- `Api`: composition root and endpoints only. No business logic.
- No MediatR, no AutoMapper, no MassTransit (licensing — see `docs/adr/`).

## Tenancy

- Every tenant-owned entity carries `TenantId` and has an EF global query filter that **fails closed**.
- Bind the tenant once per scope with `ITenantContext.Set`. Never call `IgnoreQueryFilters()` outside
  an explicitly cross-tenant admin path, and comment why when you do.

## No magic strings or numbers

- Limits, thresholds, model ids and prices live in options classes bound from configuration and
  validated at startup, or in named constants with a comment saying where the value comes from.
- Database names used in more than one place live in `Infrastructure/Persistence/PostgresNames.cs`.

## AI rules

- Model ids and per-model prices are configuration, never literals in code.
- Prompts are files under `Infrastructure/Ai/Prompts/`; any prompt change must be run through the evals.
- Retrieved text is **data, not instructions**: always delimited, never concatenated into the system prompt.
- Every LLM call goes through the cost-tracking and spend-cap pipeline.

## Tests and the verification gate

| Project | What | Needs Docker |
|---|---|---|
| `Architecture.Tests` | layer rules, forbidden references | no |
| `Domain.Tests` | pure logic: chunking, fusion, state machines | no |
| `Application.Tests` | use cases against fake ports | no |
| `Integration.Tests` | real Postgres/Mailpit via Testcontainers | yes |
| `widget/tests/widget` | the widget in Chromium against a scripted API, axe | no |
| `widget/tests/stack` | browser → real API → Postgres → Mailpit (`widget/e2e/README.md`) | yes |

Nothing is done until it ran: `dotnet build` (warnings are errors), `dotnet test`, and
`dotnet format whitespace --verify-no-changes`. For `widget/`: `npm run typecheck`, `npm run build`
(enforces the size budget) and `npm test`. "Should work" is not verified.

Widget rules: no runtime dependencies, text from the API is never inserted as HTML, every string
exists in English and Spanish (`src/i18n.ts`).

When you add a rule-like test (architecture, isolation), prove it can fail: inject the violation once,
watch it go red, revert.

## Style

- Tabs for indentation (`.editorconfig`). File-scoped namespaces. `sealed` by default.
- Comments explain *why*, not *what*. No commented-out code.
- Migrations: `dotnet ef migrations add <Name> --project src/DocsAssistant.Infrastructure --startup-project src/DocsAssistant.Api --output-dir Persistence/Migrations`.

<!-- END:managed-rules -->
