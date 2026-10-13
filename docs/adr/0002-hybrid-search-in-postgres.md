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
