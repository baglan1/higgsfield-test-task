# Memory Service

A Dockerized HTTP memory service for an AI agent, built in .NET 8. Ingests conversation turns, extracts structured knowledge with an LLM, handles fact evolution and supersession, and answers `/recall` queries under a token budget.

Built for the Higgsfield AI Engineering Challenge.

## Quick start

```bash
cp .env.example .env
# put OPENAI_API_KEY=sk-... into .env
docker compose up -d
until curl -sf http://localhost:8080/health; do sleep 1; done
# the eval can now point at http://localhost:8080
```

The default port is **8080**. All seven contract endpoints from §3 are implemented (`GET /health`, `POST /turns`, `POST /recall`, `POST /search`, `GET /users/{user_id}/memories`, `DELETE /sessions/{session_id}`, `DELETE /users/{user_id}`).

## Architecture

```
   ┌────────────────────────────────────────────────────────┐
   │                ASP.NET Core 8 minimal APIs              │
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

`POST /recall` end-to-end:

1. **Query rewrite** is gated on length (≤3 words) or pronoun/anaphora presence. Otherwise the raw query is used as the only sub-query. Saves a wasted LLM call on most queries.
2. **Hybrid retrieve** per sub-query:
   - **Vector**: `embedding <=> :q_vec` (cosine distance via HNSW), top 30, filtered to `active = true` and the requesting user.
   - **Lexical**: `ts @@ plainto_tsquery('english', :q)` ordered by `ts_rank`, top 30. The `ts` column is generated from `text`, so it stays consistent.
3. **RRF fusion** with `k = 60`, weights `vector 0.6 / lexical 0.4`. Defended below.
4. **Cross sub-query averaging**: each memory's score is averaged across sub-queries it appeared in, so multi-hop sub-queries reinforce each other rather than dominating.
5. **Multi-hop expansion**: top 10 fused results seed a 2-hop traversal of `memory_edges` with depth-decay (hop-1 = 0.6×seed, hop-2 = 0.36×seed). Pulls in the "user lives in Berlin" memory when the user asked about "the dog Biscuit's owner's city".
6. **Noise resistance**: results below `MinKeepScore = 0.005` are dropped; off-topic queries return `{"context": "", "citations": []}` with a 200.
7. **Tier-budget assembly** under `max_tokens` using `Microsoft.ML.Tokenizers` (cl100k_base, ×1.15 safety factor for non-OpenAI providers):
   - **Tier A — stable user facts** (active facts, confidence ≥ 0.7): up to **30%** of budget.
   - **Tier B — query-relevant memories** (top fused, excluding Tier A): up to **50%** of budget.
   - **Tier C — recent session context** (last 3 turns of the same session): up to **20%** of budget.
   - Spillover redistributes A → B → C; if any tier consumes less than its share, the leftover increases the next tier's allowance. This prefers stable facts over recency, but never starves recency entirely.
8. **Format** as markdown with the same section headers shown in the task example.
9. **Citations**: one per included memory with `turn_id = source_turn_id`, the fused score, and a 160-char snippet.

### Why these numbers

- **RRF k=60**: standard from the original paper; smooth enough to avoid rank-1 domination.
- **Vector 0.6 / lexical 0.4**: vector wins on paraphrase and concept queries (the common case in conversation memory); lexical helps on proper-noun and exact-token queries ("Biscuit", "Notion") where embeddings dilute the signal.
- **Tier 30/50/20**: Tier A guarantees the agent always sees the most-load-bearing facts even if the query was ambiguous; Tier B is the largest because it directly answers the asked question; Tier C is the smallest and most expendable because it overlaps with whatever the agent already has in its session context.

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
