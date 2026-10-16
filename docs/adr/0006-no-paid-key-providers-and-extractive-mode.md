# ADR-0006 · Working without a paid model: providers and extractive mode

- **Status:** accepted · 2026-10-14

## Context

The project was designed around Claude, but there is no paid API key available for development, CI
or the public demo. Options were checked rather than assumed:

- **GitHub Models** (free, authenticated with the workflow's own token) looked ideal for CI. A smoke
  workflow returned `200 text/plain "OK"` from GitHub Actions as well as locally: the service was
  **retired on 2026-07-30**. The workflow was removed.
- **Local models through Ollama** are free and real, but need several GB of RAM: fine on a laptop, not
  on a free-tier container.
- Hosted free tiers (Groq, Gemini, OpenRouter) need a sign-up key; not available today.

## Decision

The model stays behind `IChatClient` and the provider is configuration (`Ai:Provider`):

| Provider | Used for |
|---|---|
| `Ollama` | development, the answer eval, the demo video |
| `None` | public demo and CI: **extractive mode**, no model at all |
| `Anthropic` | a client who brings a key (unchanged) |

And any model failure before the first word is streamed falls back to extractive mode
(`Chat:ExtractiveFallback`), so a missing key or a provider outage never leaves a user without a
response.

**Extractive mode** quotes the sentences of the retrieved passages closest in meaning to the question,
verbatim, each with its citation. It cannot invent anything.

## What the evals said about it

Answer eval with no model (`evals/results/answers-extractive-no-model.md`): answers 100 % of the 44
answerable questions and cites the passage holding the evidence in **80 %**, with zero invalid
citations — but it abstains on only **20 %** of the unanswerable ones.

It was hypothesised that sentence similarity, being more specific than passage similarity, could let
extractive mode abstain on its own. Measured: answerable questions' best sentence scored 0.774–0.926,
unanswerable ones up to 0.848 — the ranges overlap again, and a floor that keeps every answerable
question catches 1 of 10. **Refuted.**

So extractive output is never presented as "the answer". It is introduced as *"This is the most
relevant information I found in the documents:"* and always comes with the offer to hand off to a
person. A quote that turns out irrelevant is then honest, not misleading.

## Consequences

- The public demo works at zero cost and cannot hallucinate; its weakness (answering off-topic
  questions with related quotes) is framed, measured and documented.
- Switching the demo to written answers is configuration: `Ai:Provider=Anthropic` with a key.
- The answer eval runs against any provider with the same questions, so local models can be compared
  with numbers (see the README).

## Addendum · 2026-10-14: which local model, measured

Local models run in a container (`deploy/ollama/compose.yaml`; the evals start an identical one):
image pinned to `ollama/ollama:0.40.4`, read-only root filesystem, all Linux capabilities dropped,
`no-new-privileges`, 4 GB / 4 CPU / 512 processes, port bound to `127.0.0.1` only. Models live in a
named volume and are downloaded once.

Answer eval, same 54 questions, reasoning off, 4 CPUs:

| Provider | cited the evidence | abstained (unanswerable) | false answers *with* a citation | latency p50 / p95 |
|---|---:|---:|---:|---:|
| Extractive (no model) | **80 %** | 20 % | — (quotes only) | **0.6 / 1.5 s** |
| qwen3:0.6b | 48 % | 20 % | **2** | 28 / 55 s |
| gemma3:1b | 2 % | 30 % | 0 | 30 / 49 s |
| **qwen3:1.7b** | 48 % | **60 %** | **0** | 40 / 159 s |

- **qwen3:1.7b** is the only small model worth running: it uses `NO_ANSWER` when it should, writes good
  Spanish from English sources, and never put a citation on an invented answer. Its weakness is citation
  discipline: it answers correctly *without* `[n]` about half the time (those answers are shown with
  handoff offered).
- **qwen3:0.6b** cited invented answers twice — worse than no model. **gemma3:1b** answered things like
  "1" and almost never cited. Both discarded.
- **Reasoning must be off for small models.** Measured on one question with qwen3:0.6b: reasoning off,
  9 s and a correct citation; effort Low, 60 s and a malformed one. `Chat:ReasoningEffort` defaults to
  `None`.
- **The sentinel is matched case-insensitively** since gemma3:1b answered `No_ANSWER`.
- Ollama returned HTTP 500 on one question in two different runs; the extractive fallback answered it
  — the first time the fallback was exercised by a real failure rather than a test double.

**Decision:** public demo in extractive mode (fastest, best citations, zero cost); qwen3:1.7b in the
container for development and the demo video. Runtime alternative measured on the same day:
`ghcr.io/ggml-org/llama.cpp:server-b11610` is 311 MB to download / 1.21 GB on disk versus Ollama's
3.78 GB / 9.37 GB, behind an OpenAI-compatible API — a lighter option not adopted yet.

## Addendum · 2026-10-16: Claude Haiku 5.5, measured

A key with a small prepaid balance became available, shared with other work, so the run was planned
to the call before any was made:
- A dry run (`--dry-run`) counted 57 model calls for both suites and priced them at about US$ 0.02.
- A test captured the request without a network call: thinking disabled, which Haiku 5.5 accepts at
  its default effort; no sampling parameters or prefill, which it rejects with a 400.
- Every paid run checks a shared budget ledger before the first call and appends its real usage after
  the last one.

Answer eval, same 54 questions, `claude-haiku-5-5`, `Chat:ReasoningEffort=None`:

| Provider | answered | cited the evidence | abstained (unanswerable) | invalid citations | latency p50 / p95 | cost |
|---|---:|---:|---:|---:|---:|---:|
| Extractive (no model) | 100 % | 80 % | 20 % | 0 % | 0.6 / 1.5 s | 0 |
| qwen3:1.7b | 100 % | 48 % | 60 % | 0 % | 40 / 159 s | 0 (local CPU) |
| Claude Haiku 5.5, first run | 93 % | 91 % | 100 % | 0 % | 1.1 / 2.7 s | US$ 0.018 |
| **Claude Haiku 5.5**, after the retrieval fix | **98 %** (100 % of the 43 with the evidence retrieved) | **95 %** | **100 %** | **0 %** | 1.2 / 2.3 s | US$ 0.018 (US$ 0.33 / 1,000 questions) |

- **It is the first provider that fixes both weaknesses at once:** it cites better than quoting
  verbatim (95 % against 80 %), and it abstains on every unanswerable question (extractive manages
  20 %), without a single invented citation.
- **The first run missed the pre-registered "answered ≥ 95 %" on retrieval's account, not the
  model's.** The 3 answerable questions it declined (profile-03, rmq-06, bot-02) were exactly the 3
  whose evidence hybrid retrieval left out of the top 5. Without the evidence, it said "I can't find
  that" instead of guessing. The threshold was not lowered. Two fixes followed, neither needing a model
  call:
  - **Retrieval:** the vector's first hit is now guaranteed a place in the top 5
    (`Retrieval:GuaranteedVectorHits`, measured in the README). hit@5 went from 93 % to 98 %.
  - **The metric:** the eval records whether each question's evidence was in the passages the model
    read, and gates the model on those (`answers.answeredWhenRetrievedRate` ≥ 0.95). Retrieval recall
    is gated by its own eval.

  Re-run once on the fixed pipeline: it answered 43 of 43. The only answerable question declined is
  rmq-06, which retrieval still misses.
- No answer hit the output cap (`stop_reason` was `end_turn` on every call). Input is ~2.9K tokens a
  call, almost all of it the 5 passages.
- Total spent on the evaluation: US$ 0.039 for 117 calls. That covers the smoke run, both answer runs
  and both injection runs, and every call is recorded in the shared budget ledger.

**Decision:** Claude Haiku 5.5 is the provider for written answers wherever a key and a spend cap
exist. The public demo moves to it once it is deployed with a provider-side spend limit; until then it
stays extractive. Extractive mode remains the fallback.
