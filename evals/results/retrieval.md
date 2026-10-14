# Retrieval eval

Run 2026-10-14 13:53 UTC · 44 answerable + 10 unanswerable questions over 9 documents (`evals/corpus`).

| Mode | hit@1 | hit@5 | MRR | cross-lingual hit@5 |
|---|---:|---:|---:|---:|
| Hybrid | 55 % | 93 % | 0.720 | 86 % (21 q) |
| VectorOnly | 66 % | 91 % | 0.766 | 86 % (21 q) |
| TextOnly | 36 % | 84 % | 0.544 | 71 % (21 q) |

Abstention calibration (hybrid, best cosine similarity): answerable mean 0.856 (min 0.792), unanswerable mean 0.815 (max 0.847). The ranges overlap, so similarity alone cannot decide: best single threshold 0.842 reaches only 82 % balanced accuracy. Safe floor (keeps every answerable question) Retrieval:MinSimilarity = 0.79, which alone abstains on 2/10 unanswerable questions; the rest is left to the model's grounded answer.

## Hybrid misses

- profile-03 — "¿Para cuántos municipios desarrolló el sistema de catastro?" (expected juan-gomez-profile.md; got logs-metricas-trazas.md › Logs, métricas y trazas no son tres pilares. Son una sola petición. › El sistema de prueba: Tres servicios pequeños en Node 24, para que cada número de este artículo sea reproducible…)
- rmq-06 — "¿Por qué los tutoriales publican directo al nombre de la cola?" (expected rabbitmq-part-1.md; got logs-metricas-trazas.md › Logs, métricas y trazas no son tres pilares. Son una sola petición. › Trazas: una petición, en todos los procesos que tocó: Una traza (trace) es el recorrido de una petición. Está hecha de spans, y un span es una o…)
- bot-02 — "¿Por qué el bot no usa una herramienta para publicar los comentarios?" (expected claude-pr-review-bot.md; got claude-pr-review-bot.md › A Claude PR Review Bot in C#: The Tool Use You Don't Need › Two things that will bite you › GitHub only accepts comments on lines that are in the diff: Post a review comment on a line the PR didn't touch and the API returns a 422 that names n…)
