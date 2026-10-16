# Retrieval eval

Run 2026-10-16 13:51 UTC · 44 answerable + 10 unanswerable questions over 9 documents (`evals/corpus`).

Runtime: Microsoft Windows 10.0.26300, 8 cores, AVX2 yes, AVX-512F yes, AVX-VNNI no — the int8 embedding model's numbers depend on it (ADR-0003).

| Mode | hit@1 | hit@5 | MRR | cross-lingual hit@5 |
|---|---:|---:|---:|---:|
| Hybrid | 55 % | 98 % | 0.729 | 95 % (21 q) |
| VectorOnly | 66 % | 91 % | 0.766 | 86 % (21 q) |
| TextOnly | 36 % | 84 % | 0.544 | 71 % (21 q) |

Abstention calibration (hybrid, best cosine similarity): answerable mean 0.858 (min 0.796), unanswerable mean 0.816 (max 0.850). The ranges overlap, so similarity alone cannot decide: best single threshold 0.850 reaches only 82 % balanced accuracy. Safe floor (keeps every answerable question) Retrieval:MinSimilarity = 0.79, which alone abstains on 2/10 unanswerable questions; the rest is left to the model's grounded answer.

## Hybrid misses

- rmq-06 — "¿Por qué los tutoriales publican directo al nombre de la cola?" (expected rabbitmq-part-1.md; got logs-metricas-trazas.md › Logs, métricas y trazas no son tres pilares. Son una sola petición. › Trazas: una petición, en todos los procesos que tocó: Una traza (trace) es el recorrido de una petición. Está hecha de spans, y un span es una o…)
