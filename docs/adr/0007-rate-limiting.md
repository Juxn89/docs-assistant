# ADR-0007 · Rate limiting for a public demo

- **Status:** accepted · 2026-10-14

## Context

The chat endpoint is public: the widget's key is in every page that embeds it. Without limits, one
script can monopolise the demo's CPU (local model inference takes seconds), fill the database with
conversations, or — with a paid model configured — run up a bill. The admin API is protected by a key,
but nothing stopped guessing it.

## Decision

ASP.NET Core's built-in rate limiter, as one chained global limiter (`RateLimitingSetup`):

| Limit | Default | Against |
|---|---:|---|
| Chat, per client, per minute | 10 | bursts and scripts |
| Chat, per client, per day | 60 | one visitor exhausting the demo |
| Chat, per assistant (tenant), per day | 2 000 | one embedding site starving the others |
| Admin API, per client, per minute | 60 | guessing the admin key |

- A rejected request gets **429**, a `Retry-After` header and a ProblemDetails with `code: rate_limited`.
- The limiter runs **before** the endpoint, so a request for an unknown assistant key still spends a
  permit — otherwise scanning for valid keys would be free.
- **Client identity is the connection's address.** Behind a proxy that is the proxy's address, so
  `RateLimiting:TrustForwardedHeaders=true` reads it from `X-Forwarded-For` instead — taking exactly
  one hop, the one the ingress appended. It is **off by default**: trusting that header on an app that
  is reachable directly lets anyone claim a new address on every request and reset their quota.
  Enable it only where the ingress is the only way in (Azure Container Apps).
- **IPv6 clients are grouped by /64.** A home connection or a phone receives a whole /64, so a
  per-address limit could be bypassed by rotating the low 64 bits.

Each of the three non-obvious rules (spoofed headers ignored, /64 grouping, per-tenant cap across
clients) has an integration test that was verified to fail when the rule is removed.

## Consequences

- **In memory, single instance.** Counters live in the process: correct for the demo (one replica),
  wrong for a scaled-out deployment, where each replica would grant the full quota. Scaling out means
  moving the counters to Valkey (already in the stack) — a change confined to `RateLimitingSetup`.
- Fixed windows allow up to twice the per-minute limit across a window boundary. Acceptable for abuse
  control; a sliding window costs more memory per client for no practical gain here.
- Limits are configuration, so a client deployment tunes them without code.
