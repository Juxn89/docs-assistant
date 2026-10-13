---
paths:
  - "src/DocsAssistant.Infrastructure/Ai/**"
  - "src/DocsAssistant.Application/Features/Chat/**"
---

# AI rules

- Model ids and prices are configuration (`AiOptions`), never literals.
- Prompts live in `Infrastructure/Ai/Prompts/*.md`. Changing one means running the evals and
  recording the result in `evals/results/`.
- Retrieved chunks are wrapped as `<document index="n">…</document>` and described to the model as
  data. Never interpolate document text into the system prompt.
- Every call goes through the chat client pipeline (OpenTelemetry → cost tracking → spend cap). Do not
  construct a raw client in a feature.
- Prefer abstaining ("I don't have that information") over a plausible guess. The abstention eval
  is as important as the accuracy eval.
