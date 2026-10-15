# ADR-0001 · Only OSI-licensed dependencies, chosen by license

- **Status:** accepted · 2026-10-13

## Context

This project is meant to be cloned and run by anyone, and adapted for clients. A dependency whose
license changes (or that requires a key at runtime) breaks both. The .NET ecosystem moved several
popular libraries to commercial or open-core licensing in 2025-2026.

## Decision

| Need | Choice | Rejected | Why |
|---|---|---|---|
| Database + vectors + full text | PostgreSQL + pgvector | MongoDB, SQL Server, dedicated vector DB | SSPL / non-free production licensing; one engine keeps transactions and costs at zero |
| Cache, rate limiting | Valkey (BSD-3) | Redis | Redis moved to RSALv2/SSPL in 2024 |
| In-process messaging | plain classes + `Channel<T>` | MediatR, MassTransit | MassTransit v9 is commercial (Jan 2026); MediatR is not needed for a handful of use cases |
| Mapping | hand-written | AutoMapper | open-core; mapping here is trivial |
| Embeddings | ONNX `multilingual-e5-small` (MIT) | paid embedding API | zero cost, no network hop, bilingual |
| LLM | Claude via official SDK (MIT), behind `IChatClient` | — | the port keeps the provider swappable (Ollama adapter for offline runs) |
| Widget build and tests | TypeScript (Apache-2.0), esbuild (MIT), Playwright (Apache-2.0), @axe-core/playwright (MPL-2.0, test-only) | a UI framework | nothing but our own code ships in `widget.js` (ADR-0010) |
| Email | MailKit (MIT) | `System.Net.Mail.SmtpClient` | Microsoft marks SmtpClient as not recommended for new development |

## Consequences

- Every new package is checked for license before it is added to `Directory.Packages.props`.
- The model provider is a configuration choice, not an architectural one.
