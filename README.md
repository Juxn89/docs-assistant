# docs-assistant

[![CI](https://github.com/Juxn89/docs-assistant/actions/workflows/ci.yml/badge.svg)](https://github.com/Juxn89/docs-assistant/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**An AI support assistant that answers your customers from your own documents — with citations — and
hands the conversation to a human when it doesn't know.** Embeds in any website with one line.

> 🚧 **Work in progress.** Built in public over two weeks. The table below is the honest state.

## The problem

Customers ask the same questions at all hours, and generic chatbots make answers up. A support
assistant is only useful if it **answers from your content, shows where the answer came from, and says
"I don't know" instead of guessing**.

## How it answers

1. Your documents (PDF, DOCX, Markdown, web pages) are split along their structure and indexed twice:
   as vectors (meaning) and as full text (exact words), in Postgres.
2. A question searches both, and the two rankings are fused with Reciprocal Rank Fusion.
3. If nothing relevant enough comes back, it **abstains without calling the model** — zero cost, zero
   invention — and offers to hand off to a person.
4. Otherwise Claude answers using only those passages, citing them as `[1]`, `[2]`.

## Status

| Piece | State |
|---|---|
| Domain: tenants, documents, structure-aware chunking, rank fusion | ✅ done, unit tested |
| Persistence: pgvector (HNSW, cosine) + full-text (GIN, per-language stemming), tenant isolation that fails closed | ✅ done, tested against real Postgres |
| Architecture rules enforced in CI | ✅ |
| Ingestion: PDF, DOCX, Markdown, text and web pages → chunks → embeddings, in a background worker with retries, crash recovery and dedup | ✅ end-to-end tested with the real model |
| In-process multilingual embeddings, verified against the Python reference | ✅ [ADR-0003](docs/adr/0003-in-process-embeddings.md) |
| URL ingestion hardened against SSRF (connect-time IP checks, redirects re-checked) | ✅ |
| Admin API (tenants, documents) | ✅ |
| Hybrid retrieval + evals | ⏳ |
| Streaming answers with citations, abstention, guardrails | ⏳ |
| Handoff, widget, admin panel, cost dashboard | ⏳ |
| Live demo | ⏳ — will run on [juangomezb.com](https://juangomezb.com) |

## Run it

Requires the .NET 10 SDK and Docker.

```bash
git clone https://github.com/Juxn89/docs-assistant.git && cd docs-assistant
dotnet run --project src/DocsAssistant.AppHost
```

The Aspire dashboard opens with Postgres (pgvector), Valkey, Mailpit and the API, with traces and
metrics wired.

```bash
dotnet test        # unit + architecture + integration (integration needs Docker)
```

The first integration run downloads the embedding model (~123 MB, verified by SHA-256) into the
user cache; later runs reuse it.

### Try the admin API

With the AppHost running (local admin key in `src/DocsAssistant.AppHost/appsettings.Development.json`):

```bash
KEY=local-dev-admin-key-not-a-secret-0123456789
TENANT=$(curl -s -X POST localhost:5010/v1/admin/tenants -H "X-Admin-Key: $KEY" -H 'content-type: application/json' \
  -d '{"name":"Acme","handoffEmail":"help@acme.test","defaultLanguage":"Spanish","allowedOrigins":[]}' | jq -r .id)
curl -s -X POST localhost:5010/v1/admin/tenants/$TENANT/documents -H "X-Admin-Key: $KEY" -F file=@faq.pdf
curl -s localhost:5010/v1/admin/tenants/$TENANT/documents -H "X-Admin-Key: $KEY" | jq '.[] | {title, status, chunkCount}'
```

Errors are ProblemDetails with a stable `code`: see [docs/errors.md](docs/errors.md).

## Architecture

```
Domain  ←  Application  ←  Infrastructure  ←  Api
```

- **Domain** is plain C#: chunking, fusion, document state machine. No packages at all.
- **Application** holds use cases and ports; it sees ports such as `IChatClient` (Microsoft.Extensions.AI) and
  `IEmbedder`, never a concrete model client.
- The rule is not a diagram: [`tests/DocsAssistant.Architecture.Tests`](tests/DocsAssistant.Architecture.Tests)
  fails the build when it is broken, and each rule was proven able to fail.

Decisions are recorded in [`docs/adr`](docs/adr).

## How it was built

Developed with Claude Code under explicit rules rather than ad-hoc prompting:
[`AGENTS.md`](AGENTS.md) (binding rules), [`.claude/rules`](.claude/rules), a
[`format` hook](.claude/hooks/format.sh) that enforces style on every edit, a
[`security-auditor`](.claude/agents/security-auditor.md) subagent, and every pull request reviewed by
[claude-pr-review-bot](https://github.com/Juxn89/claude-pr-review-bot).

## License

[MIT](LICENSE)
