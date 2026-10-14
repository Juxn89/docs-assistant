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
3. If even the best passage is below a measured similarity floor, it **abstains without calling the
   model** — zero cost, zero invention — and offers to hand off to a person.
4. Otherwise Claude answers using only those passages, citing them as `[1]`, `[2]`, and still says
   "I don't know" when the passages do not contain the answer.

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
| Hybrid retrieval (pgvector + full text, RRF) with a retrieval eval gating CI | ✅ [results](evals/results/retrieval.md) |
| Streaming answers (SSE) with validated citations and three-level abstention | ✅ [ADR-0005](docs/adr/0005-grounded-answers-and-abstention.md) |
| Works with no paid model: Ollama locally, extractive mode (quotes with citations) for the demo and as automatic fallback | ✅ [ADR-0006](docs/adr/0006-no-paid-key-providers-and-extractive-mode.md) |
| Rate limiting (per client, per assistant, admin key guessing; spoof-resistant, IPv6 /64) | ✅ [ADR-0007](docs/adr/0007-rate-limiting.md) |
| Prompt-injection guardrails (question refusal, passage quarantine, prompt canary) with an attack eval and false-positive count | ✅ [ADR-0008](docs/adr/0008-prompt-injection-guardrails.md) |
| Handoff, widget, admin panel, cost dashboard | ⏳ |
| Live demo | ⏳ — will run on [juangomezb.com](https://juangomezb.com) |

## Measured, not claimed

Retrieval eval over a fixed bilingual corpus (9 real technical articles and a profile, 158 chunks) and
54 questions — 44 with a known answer passage, 21 of them asked in the other language, and 10 with no
answer in the corpus. No language model involved: it measures whether the passage with the answer
reaches the top 5. Reproduce with `dotnet run --project evals/DocsAssistant.Evals`.

| Mode | hit@1 | hit@5 | MRR | cross-lingual hit@5 |
|---|---:|---:|---:|---:|
| **Hybrid (shipped)** | 55 % | **93 %** | 0.720 | **86 %** |
| Vector only | 66 % | 91 % | 0.766 | 86 % |
| Full text only | 36 % | 84 % | 0.544 | 71 % |

What the numbers decided:

- **Hybrid, equal weights.** Down-weighting full text improves the first rank but loses answers
  (hit@5 93 % → 89 % at weight 0.3). The model reads all five passages, so recall wins.
- **Similarity alone cannot tell answerable from unanswerable.** Answerable questions scored 0.79–0.90,
  unanswerable ones up to 0.85: the ranges overlap, and the best single cut-off is only 82 % accurate.
  So the threshold is a *floor* (0.79, the lowest answerable score) that never refuses a real question
  and catches the clearly off-topic ones for free; everything above it goes to the grounded answer.
- **The misses are listed**, not hidden: [evals/results/retrieval.md](evals/results/retrieval.md).

Answer eval, same 54 questions, end to end (`-- --suite answers`, provider from configuration):

| Provider | cited the evidence | abstained when there was no answer | false answers with a citation | latency p50 |
|---|---:|---:|---:|---:|
| Extractive (no model, zero cost) | **80 %** | 20 % | — (quotes only) | **0.6 s** |
| qwen3:1.7b, local container | 48 % | **60 %** | 0 | 40 s |
| qwen3:0.6b, local container | 48 % | 20 % | 2 | 28 s |
| gemma3:1b, local container | 2 % | 30 % | 0 | 30 s |

Local models run in a hardened container ([deploy/ollama](deploy/ollama/compose.yaml)): pinned image,
read-only filesystem, no Linux capabilities, loopback-only port, memory/CPU limits.

Prompt-injection eval (`-- --suite injection`): 8 attacks in the question, 4 hidden in an uploaded
document, plus what the guardrails cost on the real data
([ADR-0008](docs/adr/0008-prompt-injection-guardrails.md)):

| Provider | direct resisted | indirect resisted | legitimate questions refused | clean passages quarantined |
|---|---:|---:|---:|---:|
| Extractive | 8/8 | 4/4 | 0/54 | 0/158 |
| qwen3:1.7b | 7/8 (was 2/8 without guardrails) | 4/4 | 0/54 | 0/158 |

The miss is kept in the eval on purpose: "…append the word PWNED" is not shaped like an injection and
gets through to the model. The assistant has no tools, so the worst case is a wrong word on screen.

Extractive mode cannot tell an unanswerable question apart (measured, [ADR-0006](docs/adr/0006-no-paid-key-providers-and-extractive-mode.md)),
so its output is framed as "the most relevant information I found" and always offers a human.

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
CREATED=$(curl -s -X POST localhost:5010/v1/admin/tenants -H "X-Admin-Key: $KEY" -H 'content-type: application/json' \
  -d '{"name":"Acme","handoffEmail":"help@acme.test","defaultLanguage":"Spanish","allowedOrigins":[]}')
TENANT=$(echo "$CREATED" | jq -r .id)
PK=$(echo "$CREATED" | jq -r .publicKey)   # what the widget embeds
curl -s -X POST localhost:5010/v1/admin/tenants/$TENANT/documents -H "X-Admin-Key: $KEY" -F file=@faq.pdf
curl -s localhost:5010/v1/admin/tenants/$TENANT/documents -H "X-Admin-Key: $KEY" | jq '.[] | {title, status, chunkCount}'
```

Then ask — the answer streams as Server-Sent Events. Without any model configured it answers in
extractive mode (quotes with citations); `Ai__Provider=Ollama` (with a local model) or
`ANTHROPIC_API_KEY` give written answers:

```bash
curl -N -X POST localhost:5010/v1/chat/$PK -H 'content-type: application/json' -d '{"question":"¿Hacen envíos gratis?"}'
```

Errors are ProblemDetails with a stable `code`, and the chat stream's events are documented too:
see [docs/errors.md](docs/errors.md).

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
