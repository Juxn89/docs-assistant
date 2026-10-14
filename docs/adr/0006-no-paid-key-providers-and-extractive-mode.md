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
