# Graphiti adapter

A FastAPI adapter wrapping [getzep/graphiti](https://github.com/getzep/graphiti) — the temporal-edge knowledge graph engine that powers Zep — behind our HTTP memory contract.

Why Graphiti directly, not Zep CE: Zep CE wraps Graphiti with an app/session/chat layer that adds its own opinions about how memories surface. To isolate the temporal-KG hypothesis (the question we're actually asking), this slice uses Graphiti raw.

## Run

```bash
# from repo root
docker compose -f adapters/graphiti/docker-compose.yml --env-file .env up -d --build
until curl -sf http://localhost:8083/health; do sleep 1; done

BASE=http://localhost:8083 fixtures/run-eval.sh fixtures/locomo-real
```

Port **8083**. FalkorDB (Redis-protocol graph DB) backs Graphiti; data lives in the `falkordb-data` volume.

## How Graphiti differs

| | Our service | Graphiti |
|---|---|---|
| Atom of input | one `/turns` call | one *episode* (we map 1:1) |
| Extraction | LLM extracts memories with subject/predicate/object/aspect/stance | LLM extracts entities + relationships into a graph |
| Time model | `created_at` + `supersedes` chain | bi-temporal: `valid_at` / `invalid_at` per edge |
| Recall | RRF + multi-hop + tier-budget assembly | semantic + graph-traversal search returning `EntityEdge.fact` strings |
| Storage | Postgres + pgvector + tsvector | FalkorDB (graph) |
| User scoping | `user_id` column | `group_id` per node/edge |
| Sessions | first-class | encoded as `source_description` on episodes |

The adapter:
- maps `/turns` → `add_episode(...)` with `group_id = user_id` and `reference_time = req.timestamp` so Graphiti can timestamp the edges it derives
- maps `/recall` → `search(query=..., group_ids=[user_id])` and formats `EntityEdge.fact` strings, filtering out edges with `invalid_at` set so superseded facts don't leak
- maps `/search` → same but returns structured items instead of formatted prose
- `/users/{id}/memories` returns empty (no first-class enumeration API)
- `DELETE /users/{id}` runs raw Cypher to detach-delete all nodes with `group_id = user_id`
- `DELETE /sessions/{id}` is a no-op (Graphiti doesn't track sessions)

## Mismatches that affect interpretation

- **Graphiti's recall is graph-search, not retrieval-then-format.** It returns relationship descriptions ("Caroline lives in Berlin"), not raw memory text. The substring grader works fine with this — and may even favor Graphiti slightly because the `.fact` strings are concise.
- **Bi-temporal supersession.** Graphiti models "Caroline used to live in NYC, now lives in Berlin" as one edge with `invalid_at` set on the old fact and a new edge for Berlin. We filter out `invalid_at`-set edges in `/recall`, so only currently-valid facts surface — matching our `active=true` semantics.
- **No off-topic gate.** Graphiti will return any edges that semantically match the query. Adversarial probes will likely lose, same as mem0 and cognee.
- **Verbose ingestion.** Graphiti runs more LLM calls per episode than mem0 (entity resolution + relationship extraction + temporal reasoning). Expect ingestion to land in the 8–15 minute range for `fixtures/locomo-real`. Way over the 5-minute budget.

## Caveats with the adapter

- **Graphiti is async-first.** All operations are awaited. The adapter uses async route handlers.
- **Lazy init**: `Graphiti()` connects to the DB during construction, so we defer until first request. `build_indices_and_constraints()` runs once on first use.
- **`OPENAI_API_KEY` must be set before `import graphiti_core`** — same pattern as cognee. Done at the top of `main.py`.
- **API drift**: Graphiti has changed `add_episode` signature multiple times in 2024–2025. The adapter targets a recent stable API (`>=0.10`). If you see `TypeError` on `add_episode`, check the installed version.
- **FalkorDB version**: pinned to `falkordb/falkordb:latest`; pin to a specific tag if you need reproducibility.

## Hypotheses the eval will test

- **Temporal**: Graphiti should match or beat cognee (5/5). Bi-temporal edges are exactly the right shape for "when did X happen" questions.
- **Multi-hop**: Should be strong (3-5/5). Graph traversal via `search()` follows relationships natively.
- **Adversarial**: Likely loses (1/5) — no off-topic gate.
- **Single-hop**: Tied with mem0 / cognee, possibly lower because Graphiti's `.fact` strings are concise and may not always include the answer keyword the substring grader looks for.
- **Wall time**: 8–15 min. Set `INCLUDED_SESSIONS = sessions 1–3` in `fixtures/locomo-real/build.py` to fit a 5-minute run.
