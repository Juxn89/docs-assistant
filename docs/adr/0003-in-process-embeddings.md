# ADR-0003 · In-process embeddings with multilingual-e5-small (int8), one sequence at a time

- **Status:** accepted · 2026-10-14

## Context

Retrieval needs an embedding for every chunk at ingestion and for every question at chat time.
Claude has no embedding endpoint. Options: a paid embedding API, a sidecar (Ollama, TEI), or running
a small model inside the API process.

The demo runs on a free container tier, the corpus is bilingual (Spanish questions over English
documents and vice versa), and the budget is zero.

## Decision

- **Model:** `intfloat/multilingual-e5-small` (MIT, 384 dimensions, 100+ languages), the official ONNX
  export quantized to int8 (118 MB instead of 470 MB), pinned to Hugging Face revision
  `614241f6…` and verified by SHA-256 before loading.
- **Runtime:** ONNX Runtime inside the API. No sidecar, no network hop, no per-call cost.
- **Tokenizer:** the model's SentencePiece file through `Microsoft.ML.Tokenizers`, with the
  XLM-RoBERTa *fairseq offset* applied (special tokens 0–3, every piece shifted by one).
- **Pooling:** mean over tokens, then L2 normalization, as the model card specifies; `query: ` and
  `passage: ` prefixes as the model was trained.
- **One sequence per inference, never a padded batch.**

## Why one sequence at a time

The int8 model is *dynamically* quantized: activation scales are computed over the whole input
tensor, padding included. Measured on 2026-10-14 in Python with ONNX Runtime, the same passage
embedded alone vs. next to a longer one has a cosine similarity of **0.994** with itself. An index
whose vectors depend on what else happened to be in the batch is not reproducible: re-ingesting the
same document would change its vectors. Single-sequence inference costs a few milliseconds per chunk
at knowledge-base volumes and makes every vector a pure function of its text.

## How it is verified

The C# port is checked against the reference implementation, not against itself
(`tests/DocsAssistant.Integration.Tests/EmbedderTests.cs`):

- token ids equal Hugging Face's tokenizer on 8 cases (accents, CJK, code, whitespace, empty);
- vectors match ONNX Runtime 1.31.0 in Python (same version as the .NET package) with cosine ≥ 0.9999;
- a passage's vector is identical alone and inside a call with other passages;
- a Spanish question ranks the English answer above an unrelated English passage.

A wrong offset or pooling bug produces plausible vectors and no error; only this comparison catches it.

## Consequences

- The first request after a cold start loads the model (~1–2 s); it is downloaded once per container
  image or cache directory.
- Similarities are compressed (relevant ≈ 0.85, unrelated ≈ 0.69 on the reference pair), so the
  abstention threshold is calibrated from the evals, never guessed.
- Swapping models means a migration (column dimension) and re-embedding every chunk.
