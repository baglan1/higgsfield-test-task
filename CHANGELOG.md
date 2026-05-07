# CHANGELOG

## v2 — Tests

- Added [MultiHopExpander unit tests](tests/MemoryService.Recall.Tests/MultiHopExpanderTests.cs) covering the BFS algorithm against a Testcontainers Postgres + pgvector fixture. Includes regression tests for two specific bugs found during review: hop-decay compounding across hops, and original-seed leakage at hop ≥ 2.
- Added [recall-quality fixture runner](fixtures/run-eval.sh) — ingests `<dir>/conversations/*.json` via `/turns`, then runs `<dir>/probes/*.json` against `/recall` and grades each probe by substring match. Reports per-category pass/fail.
- Added [LoCoMo-real fixture](fixtures/locomo-real/) — a 5-minute slice of the actual [snap-research/LoCoMo](https://github.com/snap-research/locomo) `locomo10.json` dataset (1 conversation, 6 sessions, 23 probes balanced across LoCoMo's 5 QA categories). [build.py](fixtures/locomo-real/build.py) converts the upstream format to ours.

## v1 — Initial implementation

**What's in this version:**

- ASP.NET Core 8 minimal APIs implementing the seven contract endpoints (`/health`, `/turns`, `/recall`, `/search`, `/users/{id}/memories`, `DELETE /sessions/{id}`, `DELETE /users/{id}`).
- PostgreSQL 16 + pgvector + tsvector as the single backing store. Schema bootstrapped at startup with raw SQL: HNSW index on `embedding`, GIN index on the generated `ts` column, `pg_trgm` GIN on `subject`, unique partial index on the active `(user_id, subject, predicate, aspect)` triple, and a `memory_edges` table for multi-hop.
- Pluggable LLM via `IChatLlm` / `IEmbedder` with three backends: OpenAI (default), Anthropic, Ollama. Lazy construction so DI succeeds without a key; first call surfaces a configuration error.
- `/turns` pipeline: persist session + turn → run extractor LLM → batch-embed candidates → run supersession judge → apply ADD / UPDATE / DEDUP / COEXIST → write derived edges. Synchronous, with a 55 s soft deadline inside the eval's 60 s budget; on timeout the turn is still persisted.
- Supersession via `active=false` + `supersedes=old.id`; aspect/stance columns let opinion arcs use the same mechanism (same aspect → UPDATE, different aspect → COEXIST).
- `/recall` pipeline: gated query rewrite → per sub-query hybrid retrieve (pgvector cosine + `ts_rank`) → RRF fusion (k=60, vector 0.6 / lexical 0.4) → 2-hop `memory_edges` expansion with depth decay → tier-budget assembly (A 30% stable facts / B 50% query-relevant / C 20% recent turns) using `Microsoft.ML.Tokenizers`.
- Cold-session and noise-resistance shortcuts: `/recall` and `/search` return 200 empty when the user has no memories, without invoking the LLM.
- Bearer auth middleware (open when `MEMORY_AUTH_TOKEN` unset).
- Docker Compose with named volume for persistence across restarts.
- 6 contract tests passing against Testcontainers Postgres with a fake LLM. Recall / integration / concurrency test projects scaffolded but not yet populated.

**Verified manually against the live stack:**
- Schema bootstrap (HNSW + GIN + tsvector generated column + unique partial index).
- `/health` returns 200.
- `/turns` returns 201 and persists the turn even with no LLM key.
- `/recall` and `/search` return 200 empty on cold sessions.
- `DELETE /sessions/{id}` and `DELETE /users/{id}` return 204 and cascade correctly.
- Persistence: turn count survives `docker compose down && up`.
