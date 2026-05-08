# CHANGELOG

## v4 — Pruned default pipeline based on ablation findings

- Swept all 22 configurations from [docs/recall-configs.md](docs/recall-configs.md) against `fixtures/locomo-real` via [fixtures/ablation-runner.sh](fixtures/ablation-runner.sh) (bind-mounts `appsettings.json`, restarts the service per config, reuses pgvector data across configs after the first ingest). Per-config CSV at [fixtures/ablation-results.csv](fixtures/ablation-results.csv); narrative summary appended to [fixtures/RESULTS.md](fixtures/RESULTS.md) under "Ablation sweep on locomo-real".
- Found that the previous 9-stage default was not pareto-optimal. Removing `VectorSimilarityFloor` lifts total 7 → 12 (multi-hop 2 → 5) — the 0.45 floor was short-circuiting queries whose top raw cosine sat just below threshold. The best-scoring config (`factual_lookup`: `HybridRetrieval, MultiHopExpansion, LowScoreFilter, TierBRelevantMemories, Assembly` with `MultiHopMaxHops=3`) reaches 13/23 — 4/5 single-hop, 5/5 multi-hop, 2/5 temporal — without `QueryRewrite`, `VectorSimilarityFloor`, `TierAStableFacts`, or `TierCRecentSession` running.
- Promoted `factual_lookup` to the default in [appsettings.json](src/MemoryService.Api/appsettings.json). The dropped stages stay registered in DI, so any caller can re-enable them per-config without redeploying.
- Caught and fixed an `IConfiguration`-binding bug surfaced by the sweep: `List<T>` properties APPEND to in-code defaults rather than replace them, so configs like `["HybridRetrieval", "Assembly"]` were silently merging with the 9-stage default and running everything anyway. Fix: default `Stages` is now an empty list ([RecallPipelineOptions.cs](src/MemoryService.Recall/RecallPipelineOptions.cs)); the real defaults live only in `appsettings.json`. Without this fix the sweep produced uniform scores and the rest of the analysis would have been meaningless.
- Pure stage reorderings (`floor_after_multihop`, `filter_before_multihop`, `recent_first`, `floor_last_in_shaping`) all collapse to the same 7/23 as default — useful sanity check that score deltas elsewhere are signal, not config drift.
- Adversarial caveat: the new default scores 2/5 adversarial on this ingest run (vs the previous 9-stage default's 5/5 in earlier runs). Removing the floor trades adversarial robustness for retrieval recall. For deployments where off-topic refusal matters more than recall, switch back to a config containing `VectorSimilarityFloor`. The trade-off is now an explicit knob, not a hidden default.

## v3 — Stage-based recall pipeline

- Refactored `RecallPipeline.RecallAsync` from a monolithic method into a stage-driven pipeline. Each step (`QueryRewrite`, `HybridRetrieval`, `VectorSimilarityFloor`, `MultiHopExpansion`, `LowScoreFilter`, `TierAStableFacts`, `TierBRelevantMemories`, `TierCRecentSession`, `Assembly`) is now a separate [`IRecallStage`](src/MemoryService.Recall/IRecallStage.cs) class under [`src/MemoryService.Recall/Stages/`](src/MemoryService.Recall/Stages/). State flows between stages via a mutable [`RecallContext`](src/MemoryService.Recall/RecallContext.cs).
- Stages are listed in [`RecallPipelineOptions.Stages`](src/MemoryService.Recall/RecallPipelineOptions.cs) and bound from the `Recall:Stages` section of [`appsettings.json`](src/MemoryService.Api/appsettings.json). To turn a stage off, remove its name. To reorder, change positions. To tune, edit the per-stage values in the same options class.
- Default `Stages` list reproduces the previous monolithic behavior. Verified against `fixtures/locomo-real`: 10/23 in 4:20 (within LLM-extraction variance of the pre-refactor 9/23 run).
- Added [`docs/recall-configs.md`](docs/recall-configs.md) — catalogue of 22 named pipeline configurations covering single-stage ablations, signal-shaping reorderings, tier-construction variants, aggressive ablations (e.g. `floor_only`, `baseline_inside`), and production-tuning suggestions. Each config is a copy-pasteable JSON array with a stated hypothesis.

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
