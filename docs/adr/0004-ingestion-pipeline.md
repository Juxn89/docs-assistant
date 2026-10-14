# ADR-0004 · Ingestion: the database is the queue of record

- **Status:** accepted · 2026-10-14

## Context

Uploading a document must return quickly; extracting, chunking and embedding it takes seconds. The
work must survive restarts and deploys, retry transient failures, never produce duplicate chunks, and
never run twice concurrently for the same document.

## Decision

1. **Registration is synchronous and transactional.** The document row and its raw bytes
   (`document_contents`, `bytea`) commit together. The content hash is the idempotency key: the same
   bytes uploaded twice to one tenant are one document. URLs are fetched at registration so they get
   the same idempotency and fail in front of the admin, not in a retry loop.
2. **An in-process bounded channel delivers work immediately**, and a **periodic sweep** re-delivers
   whatever is still `Pending`. The channel is a latency optimisation; the `status` column is the truth.
   No message broker: at this scale it would add a failure mode without adding a guarantee.
3. **Claiming is an optimistic write.** `Pending → Processing` is saved before any work, guarded by
   Postgres' `xmin` as a concurrency token. Two workers can race; one wins, the other skips.
4. **A claim is a lease.** `processing_started_at` records it; the sweep releases claims older than
   `Ingestion:ProcessingLease` (15 min). An abandoned attempt still counts, so a document that crashes
   the worker every time ends `Failed` instead of looping.
5. **Completion is atomic.** Old chunks are deleted, new chunks inserted and the document marked
   `Ready` in one transaction. On any failure, unsaved changes are discarded and the persisted state
   reloaded before the failure is recorded, so a half-applied `Ready` can never be saved alongside it.

## Consequences

- Throughput is one document at a time per instance — enough for a knowledge base, and the sweep
  makes horizontal scale-out safe because claims are optimistic.
- Raw bytes live in Postgres. At the 10 MB cap that is simpler than a blob store; past it, the
  `IDocumentRepository` port is where a blob adapter would go.
