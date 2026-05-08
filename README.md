# Memory Service

A Dockerized HTTP memory service for an AI agent, built in .NET 10. Ingests conversation turns, extracts structured knowledge with an LLM, handles fact evolution and supersession, and answers `/recall` queries under a token budget.

Built for the Higgsfield AI Engineering Challenge.

## Quick start

You need Docker (with Compose) and an OpenAI API key. Postgres, the .NET runtime, and all dependencies come from the compose stack — nothing else to install locally.

```bash
# 1. Create your local env file from the template
cp .env.example .env

# 2. Edit .env and put your OpenAI key in:
#       OPENAI_API_KEY=sk-...
#    The other vars in the template have sensible defaults; the only required
#    edit is OPENAI_API_KEY (or the Anthropic/Ollama equivalent if you switch
#    MEMORY_LLM_PROVIDER). MEMORY_AUTH_TOKEN is optional — leave it empty for
#    open access, or set it (e.g. MEMORY_AUTH_TOKEN=token123) to require a
#    bearer token on every endpoint except /health.

# 3. Bring up the stack (memory-service + postgres with named volume)
docker compose up -d

# 4. Wait for health
until curl -sf http://localhost:8080/health; do sleep 1; done

# 5. Smoke test
curl -X POST http://localhost:8080/turns -H 'Content-Type: application/json' \
  -d '{"session_id":"smoke","user_id":"u1","messages":[{"role":"user","content":"I just moved to Berlin"}],"timestamp":"2026-05-08T12:00:00Z","metadata":{}}'

curl -X POST http://localhost:8080/recall -H 'Content-Type: application/json' \
  -d '{"query":"where does the user live?","session_id":"smoke","user_id":"u1","max_tokens":512}'
```

If you set `MEMORY_AUTH_TOKEN`, prepend `-H "Authorization: Bearer $MEMORY_AUTH_TOKEN"` to every request except `/health`.

The default port is **8080**. All seven contract endpoints from §3 are implemented (`GET /health`, `POST /turns`, `POST /recall`, `POST /search`, `GET /users/{user_id}/memories`, `DELETE /sessions/{session_id}`, `DELETE /users/{user_id}`).

To run the recall fixture against a live stack:

```bash
fixtures/run-eval.sh fixtures/locomo-real
# or, if memories are already ingested and you only want to re-grade probes:
SKIP_INGEST=1 fixtures/run-eval.sh fixtures/locomo-real
```

## Architecture

```
   ┌────────────────────────────────────────────────────────┐
   │               ASP.NET Core 10 minimal APIs              │
   │ /health  /turns  /recall  /search  /users/...  /delete │
   └─────────────┬────────────────┬───────────────┬─────────┘
                 │                │               │
        ┌────────▼────────┐ ┌─────▼──────┐ ┌──────▼─────────┐
        │  TurnService    │ │  Recall    │ │  Search        │
        │ extract→judge   │ │  pipeline  │ │  service       │
        │ →supersede→edge │ │            │ │                │
        └────────┬────────┘ └─────┬──────┘ └──────┬─────────┘
                 │                │               │
            ┌────▼────────────────▼────────────┐  │
            │  IChatLlm + IEmbedder            │  │
            │  OpenAI | Anthropic | Ollama     │  │
            └────┬─────────────────────────────┘  │
                 │                                │
        ┌────────▼────────────────────────────────▼────────┐
        │     PostgreSQL 16 + pgvector (HNSW) + tsvector   │
        │  sessions / turns / memories / memory_edges      │
        └──────────────────────────────────────────────────┘
```

The service is a single .NET process. Postgres is the only backing store — pgvector handles semantic search via an HNSW index, a generated `tsvector` column with a GIN index handles keyword search, and a small `memory_edges` table covers multi-hop retrieval. There is no separate vector DB and no graph database.

## Backing store choice

**PostgreSQL 16 + pgvector + tsvector** in a single instance.

Why one store instead of, say, Qdrant for vectors and Neo4j for the graph:

- **Transactional supersession.** Marking a memory inactive *and* inserting its replacement *and* writing edges have to be atomic. Two stores means two-phase coordination problems we don't need at this scale.
- **Hybrid retrieval is cheap.** Vector top-k and BM25-ish ranking from `ts_rank` are both single-query SQL. RRF fusion happens in the app — no cross-store roundtrips.
- **Multi-hop fits in SQL.** A small `memory_edges` table plus a recursive CTE (or a 2-hop loop in code, as we use) gives "user owns Biscuit, user lives in Berlin → city of dog-owner = Berlin" without a graph DB.
- **One operational concern.** One image, one volume, one healthcheck.

Tradeoff: if the workload grew to millions of users with very long memory tails per user, a dedicated vector store with quantization would beat HNSW-on-Postgres on cost. Out of scope here — see §12 of the task ("No horizontal scalability proofs").

## Extraction pipeline

`POST /turns` is fully synchronous. The 60-second eval budget is honored with a 55-second soft deadline; on timeout the turn is still persisted and 201 returned, with extraction logged as degraded.

1. **Persist** the session (if new) and the turn in one transaction. The turn is recallable as raw text immediately.
2. **Extract**: a single LLM call with the conversation messages (sandbox-quoted to resist prompt injection) and the user's last 10 active memories. Returns JSON candidates of shape:
   ```
   { type, subject, predicate, object, aspect, stance, text, confidence, derived_edges }
   ```
   Types: `fact | preference | opinion | event`. Subjects are canonical lowercase (e.g. `user`, `biscuit`). Aspects model facets ("typescript.generics") so opinion arcs work uniformly with facts.
3. **Embed** all candidates in a single batch call.
4. **Judge** per candidate: a small LLM call against existing similar memories (top-K by vector + same subject) returns one of `ADD | UPDATE | DEDUP | COEXIST`.
5. **Apply**:
   - `ADD` / `COEXIST` → insert new active memory.
   - `UPDATE` → set old memory `active = false`, insert new with `supersedes = old.id`, all in one transaction.
   - `DEDUP` → drop.
   - Safety net: a unique partial index on `(user_id, subject, predicate, aspect) WHERE active` plus a fallback collision check enforces "exactly one active row per triple" even if the judge misfires under concurrency.
6. **Edges**: any `derived_edges` from the candidate are written to `memory_edges`, resolving the `dst_subject` to an existing memory id when possible.

What we extract well: explicit facts (job, location, pets), preferences with stance, opinion arcs through `aspect`, implicit facts ("walking Biscuit" → owns/biscuit), corrections (the LLM emits the corrected memory only).

What we miss: long-running narrative events that span many turns; first-class temporal validity (we have `created_at`/`updated_at` and supersession but not bi-temporal valid-from/valid-to ranges).

## Recall strategy

`POST /recall` is a **stage-driven pipeline**. Each step is a separate `IRecallStage` class registered in DI; the order and on/off state of every step come from `Recall:Stages` in [appsettings.json](src/MemoryService.Api/appsettings.json). Per-stage tunables (`PerSubqueryK`, `MultiHopMaxHops`, `MaxRankedTake`, etc.) live in [RecallPipelineOptions](src/MemoryService.Recall/RecallPipelineOptions.cs) under the same `Recall` section. Source: [src/MemoryService.Recall/Stages/](src/MemoryService.Recall/Stages/) and [RecallPipeline.cs](src/MemoryService.Recall/RecallPipeline.cs).

### Default configuration

After the v4 ablation sweep ([fixtures/RESULTS.md](fixtures/RESULTS.md), [fixtures/ablation-results.csv](fixtures/ablation-results.csv)), the default stage list is the `factual_lookup` configuration — pruned from the original 9-stage pipeline because the previous default was *not* pareto-optimal on the LoCoMo fixture. Removing the vector floor and Tier-A pre-load gained 6 probes' worth of recall (7/23 → 13/23).

```jsonc
"Recall": {
  "Stages": [
    "HybridRetrieval",
    "MultiHopExpansion",
    "LowScoreFilter",
    "TierBRelevantMemories",
    "Assembly"
  ],
  "PerSubqueryK": 30,
  "MultiHopMaxHops": 3,
  "MultiHopSeedTopK": 10,
  "MaxRecentTurns": 3,
  "MinKeepScore": 0.005,
  "VectorSimilarityFloor": 0.45,
  "MaxRankedTake": 40,
  "StableFactConfidence": 0.7,
  "StableFactsTopK": 20
}
```

### Stages

The following stages are available; the default pipeline uses the bolded ones.

1. `QueryRewrite` — gated on length (≤3 words) or pronoun/anaphora presence. Expands ambiguous queries into 2–3 sub-queries via LLM. Disabled by default: it cost a call without consistently moving the score on the fixture.
2. **`HybridRetrieval`** — per sub-query: vector top-K (`embedding <=> :q_vec` via HNSW, `PerSubqueryK = 30`) + lexical top-K (`ts @@ plainto_tsquery`, ordered by `ts_rank`), fused with RRF (`k = 60`, vector 0.6 / lexical 0.4). Cross-sub-query scores are averaged so reinforcement happens through agreement, not domination.
3. `VectorSimilarityFloor` — short-circuits with empty context if the top raw cosine across sub-queries is below `VectorSimilarityFloor = 0.45`. Disabled by default: on the fixture, the threshold was overshooting and dropping queries whose top match sat just below 0.45 even when hybrid + multi-hop would have surfaced the answer. Re-enable in deployments that prioritise off-topic refusal over recall.
4. **`MultiHopExpansion`** — seeds the top `MultiHopSeedTopK = 10` fused results into a BFS over `memory_edges` up to `MultiHopMaxHops = 3` (default was 2, raised to 3 in v4). Hop-N gets `seed × 0.6^N` (decay does *not* compound across hops; original seeds never appear in the output — both regression-tested in [MultiHopExpanderTests](tests/MemoryService.Recall.Tests/MultiHopExpanderTests.cs)).
5. **`LowScoreFilter`** — drops any candidate below `MinKeepScore = 0.005`, then caps to `MaxRankedTake = 40`. Populates `RecallContext.Ranked`, which downstream Tier B / C stages read from.
6. `TierAStableFacts` — selects up to `StableFactsTopK = 20` active facts with `confidence ≥ StableFactConfidence = 0.7` for prepending under "Known facts about this user". Disabled by default — on this fixture it was diluting tier-B's budget more often than it was helping.
7. **`TierBRelevantMemories`** — query-relevant memories from `Ranked`, formatted under "Relevant from recent conversations".
8. `TierCRecentSession` — the last `MaxRecentTurns = 3` turns from the same session. Disabled by default — the agent typically already has these in its session context.
9. **`Assembly`** — packs Tiers A/B/C into a markdown context under `max_tokens` using `Microsoft.ML.Tokenizers` (cl100k_base, ×1.15 safety factor for non-OpenAI providers). Tier weights are 30% / 50% / 20%; spillover redistributes A → B → C. Returns the citation list (one per memory: `turn_id`, fused score, 160-char snippet).

### Reconfiguring the pipeline

Edit `Recall:Stages` in `appsettings.json` (or override via the standard ASP.NET configuration providers — env vars `Recall__Stages__0=...`, `Recall__Stages__1=...`, etc.). Stage names match the `Name` property on each `IRecallStage` (case-insensitive). Unknown names are skipped with a warning.

A catalogue of named configurations — `default`, `no_floor`, `factual_lookup`, `chat_with_recency`, etc. — lives in [docs/recall-configs.md](docs/recall-configs.md), and the sweep harness for measuring them is [fixtures/ablation-runner.sh](fixtures/ablation-runner.sh).

> ⚠️ `Recall:Stages` is bound from configuration as a list. ASP.NET Core's `IConfiguration` *appends* to in-code defaults rather than replacing them, so the in-code default for `Stages` is intentionally an empty list — the real default lives only in `appsettings.json`. If you add a new override file, set `Stages` explicitly; don't rely on inheritance.

### Why these numbers

- **RRF k=60**: standard from the original paper; smooth enough to avoid rank-1 domination.
- **Vector 0.6 / lexical 0.4**: vector wins on paraphrase and concept queries (the common case in conversation memory); lexical helps on proper-noun and exact-token queries ("Biscuit", "Notion") where embeddings dilute the signal.
- **Tier 30/50/20**: Tier A guarantees the agent always sees the most-load-bearing facts even if the query was ambiguous; Tier B is the largest because it directly answers the asked question; Tier C is the smallest and most expendable because it overlaps with whatever the agent already has in its session context.
- **MultiHopMaxHops = 3** (vs prior 2): two hops covered the "user owns Biscuit → user lives in Berlin" pattern, but the LoCoMo fixture has chains of three (event → person → relationship → outcome). Raised to 3 with the same per-hop decay.
- **VectorSimilarityFloor disabled**: on the LoCoMo fixture, removing the floor lifted multi-hop from 2/5 → 5/5 with no adversarial regression on the same ingest. The trade-off is data-dependent (an earlier ingest hit 5/5 adversarial *with* the floor); leave it enabled if your traffic is noisy.

## Fact evolution

Three mechanisms interact to handle contradictions, corrections, and opinion arcs:

- **Supersession chain**. A new memory that overrides an old one is inserted with `supersedes = old.id`; the old row is set to `active = false`. The full chain is preserved and visible via `GET /users/{user_id}/memories`.
- **Aspect-based opinion arcs**. "I love TypeScript" → "TypeScript generics annoy me" → "TypeScript is fine for big projects" are three memories with the *same* subject (`typescript`) but *different aspects* (`null`, `generics`, `big_projects`). They COEXIST. Within a single aspect, a stance shift triggers UPDATE. The history is intact and the current `/recall` shows the latest stance per aspect plus historical opinions when relevant.
- **Concurrency safety net**. Two parallel `/turns` from the same user could race on superseding the same fact. The unique partial index `memories_active_triple` guarantees at most one active row per `(user, subject, predicate, aspect)`. On a unique-violation, the loser re-reads and retreats — see `TurnService.ApplyCandidateAsync`.

`/recall` only retrieves rows with `active = true`, so stale facts never leak into the agent's prompt.

## LLM providers

`MEMORY_LLM_PROVIDER` selects: `openai` (default) | `anthropic` | `ollama`.

| Provider | Chat | Embeddings | Notes |
|---|---|---|---|
| OpenAI | ✅ `gpt-4o-mini` (default) | ✅ `text-embedding-3-small` (1536 dim) | Recommended. Cheapest. |
| Anthropic | ✅ Claude (configurable) | ❌ Anthropic ships no embedding model — falls back to OpenAI key if set | |
| Ollama | ✅ via `OLLAMA_HOST` | ✅ `nomic-embed-text` (768 dim — set `MEMORY_EMBED_DIM=768`) | Fully offline. |

Embedding dimension is bootstrapped at first DB init based on `MEMORY_EMBED_DIM`. If you change the model, change the dim and re-create the volume — the initializer fails fast on mismatch.

## Failure modes

- **No LLM key**: provider factory throws at startup with a specific error naming the missing env var.
- **LLM call fails or times out**: extraction degrades to "turn persisted, no memories". `/recall` falls back to keyword-only retrieval if the embedder fails. Errors are logged at WARNING; the service stays up.
- **Postgres unreachable at boot**: initializer retries for 30 s, then fails fast.
- **Volume already initialized with different embedding dim**: initializer fails at boot with a clear remediation message.
- **Malformed input**: caught by `ProblemMiddleware` → 400 JSON, no crash.
- **Cold session / off-topic query**: `/recall` returns 200 with empty context and empty citations.

## Concurrency model

- Sessions are scoped per `session_id`. Two different sessions never bleed into each other's `/recall` because the cross-session bridge is the user's *active facts* (Tier A), which is intentional and documented.
- Same-`user_id` sharing across sessions is the deliberate design: a user's facts learned in session A inform session B's `/recall`. This is what the task example asks for ("works at Notion as a PM" applies regardless of which session asked).
- Concurrent `/turns` are protected by the unique partial index and the per-candidate transaction. Tested in `MemoryService.Concurrency.Tests`.

## Run the tests

```bash
dotnet test
```

Test projects:
- `MemoryService.Contract.Tests` — endpoint shapes, status codes, malformed input, cold-session behavior. Uses Testcontainers Postgres + a fake LLM (no external API needed).
- `MemoryService.Integration.Tests` — full `/turns → /recall` round-trip with persistence-across-restart.
- `MemoryService.Recall.Tests` — data-driven theories from `fixtures/` measuring "X of Y expected facts in returned context".
- `MemoryService.Concurrency.Tests` — 50 parallel `/turns` from the same user, asserts one active row per triple.

The recall fixtures are documented in `fixtures/README.md`.

## Tradeoffs

What we optimized for:
- **Recall quality** on multi-session conversational memory.
- **Single-store simplicity** for ops and atomicity.

What we gave up:
- **Bi-temporal queries** ("what did the user think on March 15?"). The supersession chain has it, but `/recall` doesn't expose temporal selection — out of scope.
- **Cross-encoder rerank by default**. It's wired up behind `MEMORY_RERANK=1`, but disabled by default to keep `/recall` under a second on the eval. We picked one extra LLM call (rewrite) over two (rewrite + rerank).
- **Embedding flexibility**. Dim is fixed at first init. Re-embedding the corpus on a model swap would need an admin endpoint, not built.
