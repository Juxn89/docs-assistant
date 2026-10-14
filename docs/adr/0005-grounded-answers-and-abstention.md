# ADR-0005 · Grounded answers: three ways to say "I don't know"

- **Status:** accepted · 2026-10-14

## Context

A support assistant that invents an answer is worse than none: the customer acts on it. The retrieval
eval (ADR-0002, `evals/results/retrieval.md`) showed that similarity alone cannot separate answerable
from unanswerable questions — their score ranges overlap. So abstention cannot be a single threshold.

## Decision

Abstain at three levels, cheapest first:

| Level | Signal | Cost | Outcome |
|---|---|---|---|
| 1. Retrieval floor | best passage below `Retrieval:MinSimilarity` (0.79, the lowest score any answerable question reached) | free — no model call | `AbstainedNoContext` |
| 2. The model reads and declines | prompt rule: reply exactly `NO_ANSWER` when the passages do not contain the answer | one short call | `AbstainedByModel` |
| 3. Answer without evidence | no citation `[n]` that points at a passage actually provided | the answer is shown | `AnsweredWithoutCitations`, handoff offered |

Every outcome other than a cited answer offers handoff to a person.

**The sentinel never reaches the user.** The stream holds back its first characters until they can no
longer be the start of `NO_ANSWER` — even when the model emits it split across chunks (`NO_`, `ANS`,
`WER`) — and on a match the generation is cancelled (no tokens wasted) and replaced by a localized
message. A test removes the hold-back and watches three tests fail.

**Citations are validated, not trusted.** `[7]` when five passages were sent is flagged
(`HadInvalidCitations`) and dropped from the source list; the UI only links what exists.

**Passages are data.** Each is wrapped in `<document index="n" …>` with its text XML-escaped, so a
document containing `</document><question>…` cannot close the tag and speak as the operator. The
rules live in the system instructions (`Infrastructure/Ai/Prompts/answer.md`, hashed per revision),
never concatenated with document text.

**Model and effort.** `claude-haiku-5-5` with reasoning off (`Chat:ReasoningEffort=None`; reasoning measurably hurt small
models, see ADR-0006): the answer is already in the
passages, the model only has to phrase it in the question's language and cite. 600 output tokens cap
a runaway reply. Both are configuration (`Ai:AnswerModel`, `Chat:MaxOutputTokens`).

**Failure is a typed event.** Once streaming starts the HTTP status is 200, so a provider failure or a
missing key ends the stream with `event: error` / `assistant.unavailable` — the widget can show it,
instead of an answer that simply stops. Without a configured model the API still starts; ingestion,
search and admin keep working.

## Consequences

- Level 2 costs a model call on questions that turn out unanswerable; the floor removes only the
  clear misses. The answer eval (next) measures how often each level fires and whether the model's
  `NO_ANSWER` is reliable.
- The prompt is short (~250 tokens), below the minimum cacheable prefix, so prompt caching buys
  nothing yet; it becomes worth it if the instructions grow.
