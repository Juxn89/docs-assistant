# ADR-0002 · Hybrid search in Postgres, fused by rank

- **Status:** accepted · 2026-10-13

## Context

Support questions mix meaning ("how do I send something back?") with exact tokens (product codes,
policy names, "30 días"). Vector search is good at the first and weak at the second; full-text search
is the opposite.

## Decision

- Store both representations of each chunk in the same row in Postgres:
  - `embedding vector(384)` with an **HNSW** index over cosine distance;
  - `search_vector tsvector`, a **stored generated column** with a **GIN** index, built with the
    document's language (`spanish` / `english`) so stemming matches "devolución" to "devoluciones".
- Run both searches and fuse them with **Reciprocal Rank Fusion** (k = 60).
- Filter by `tenant_id` first, in both searches.

## Why fuse ranks and not scores

Cosine similarity and `ts_rank` live on unrelated scales. Adding or normalizing them rewards whichever
retriever produces bigger numbers on a given query. RRF only uses positions, which are comparable.

## Why not a dedicated vector database

One engine means one backup, one connection string, transactional ingestion (a document and its chunks
commit together), tenant filtering with ordinary SQL, and a free tier that includes pgvector. At the
scale of a support knowledge base (thousands to low millions of chunks) HNSW in pgvector is not the
bottleneck.

## Consequences

- The generated column uses a literal `CASE` on the language: a `text → regconfig` cast is not
  immutable and Postgres rejects it in a generated column.
- Changing the embedding model is a migration (column dimension) plus a re-embed of every chunk.

## Addendum · 2026-10-14: filtered HNSW, measured

What happens when a tenant filter meets the HNSW index, measured with pgvector 0.8.7 / Postgres 18
in manual probes (not hypothetical):

| Situation | Plan Postgres chose | Rows (limit 5) |
|---|---|---|
| Small or mid-size tenant (1 / 400 of 2,400 rows) | btree on `tenant_id` + exact sort | 5 — exact |
| Dominant tenant, question near another tenant's data, **8 dims** | HNSW, filter after | **0**, with or without `iterative_scan = strict_order` |
| Same, **384 dims** (our real dimension) | HNSW, filter after | 5 with `strict_order` |
| Same, exact scan (`enable_indexscan = off`) | sort over the tenant's rows | 5 |

Consequences in the code (`PgChunkSearch`):

- `SET LOCAL hnsw.iterative_scan = strict_order` on every vector query: it keeps walking the graph
  until the limit is filled, which fixes the common shortfall.
- **Exact fallback**: if HNSW returns fewer rows than the limit *and* fewer than the tenant owns,
  the query is re-run with index scans disabled. pgvector stops iterative scans at
  `hnsw.max_scan_tuples` (20,000), so a small tenant in a large table can still come back short; the
  fallback costs one scan of that tenant's rows, only when needed.
- The per-tenant btree index matters more than either: for selective tenants the planner uses it and
  the result is exact by construction.

What the automated suite pins is the *result* (a dominant tenant gets its own nearest chunks); the
underfill path itself was only reproducible in the low-dimension probe above, and is defence in depth.

## Addendum · 2026-10-16: the vector's first hit always reaches the model

Fusion rewards agreement between the two rankings, and that fails across languages. A Spanish question
about an English article has no words in common with it. Full-text search still returns passages, the
ones sharing stray terms (names, numbers, product words), and their agreement with the vector's lower
ranks outscores a passage that only the vector ranks first.

Measured on the retrieval eval, with a temporary diagnostic that followed the evidence beyond the top 5:

| Question (Spanish, article in English) | vector-only rank | hybrid rank |
|---|---:|---:|
| profile-03 | 1 | 7 |
| bot-02 | 1 | 11 |
| rmq-06 | > 20 | > 20 |

**Decision:** `Retrieval:GuaranteedVectorHits` (default **1**). When fusion left the vector's best hit out
of the top K, it takes the place of the last fused passage, so the fused order is kept for the rest.

| Variant | hit@1 | hit@5 | MRR | cross-lingual hit@5 |
|---|---:|---:|---:|---:|
| Fusion only | 55 % | 93 % | 0.720 | 86 % |
| Text weight 0.5 / 0.3 instead | 64 % / 66 % | 91 % / 89 % | 0.749 / 0.755 | 81 % / 81 % |
| **1 guaranteed vector hit** | 55 % | **98 %** | **0.729** | **95 %** |
| 2 guaranteed vector hits | 55 % | 95 % | 0.723 | 90 % |

The similarity floor (0.79) and the unanswerable questions it catches are unchanged. rmq-06 stays out:
vector search does not have it in its top 20 and full-text ranks it 17th. It would need the question
translated before the search, which is not worth a model call per question today. The CI gate was raised to hit@5 ≥ 0.95 and cross-lingual ≥ 0.90.
