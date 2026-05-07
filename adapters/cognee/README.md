# cognee adapter

A FastAPI service wrapping [topoteretes/cognee](https://github.com/topoteretes/cognee) behind our HTTP memory contract. Lets [`fixtures/run-eval.sh`](../../fixtures/run-eval.sh) grade cognee with the same probes used to grade our service and mem0.

## Run

```bash
# from repo root
docker compose -f adapters/cognee/docker-compose.yml --env-file .env up -d --build
until curl -sf http://localhost:8082/health; do sleep 1; done

BASE=http://localhost:8082 fixtures/run-eval.sh fixtures/locomo-real
```

The adapter is its own compose project on port **8082** (mem0 is on 8081, your service on 8080). All three can run side-by-side.

## How cognee differs from our service

| | Our service | cognee |
|---|---|---|
| Ingest model | per-turn extraction + judge | stage text via `add()`, batch-process via `cognify()` |
| Retrieval | hybrid (vector + BM25) + RRF + multi-hop edges | KG-completion (LLM reads the constructed graph) |
| Storage | Postgres + pgvector + tsvector | LanceDB + sqlite by default; can use Postgres |
| Memory listing | structured rows with type/key/value/etc. | no first-class memory listing; KG is queryable but not enumerable |
| Sessions | first-class | none |

The adapter calls `cognee.add()` followed by `cognee.cognify()` per `/turns` so a subsequent `/recall` sees the new data — matching our synchronous-correctness contract. This makes ingestion slow because cognify reprocesses the dataset each call; cognee is built for batch ingestion, not turn-by-turn.

## Mismatches that affect interpretation

- **`/recall` returns an LLM completion, not a memory list.** cognee's `GRAPH_COMPLETION` search type asks the LLM to answer the query directly using the KG. Our adapter returns that completion text under "Known facts about this user". Probes that grade against substring presence will see whatever the LLM chose to surface; probes expecting a specific memory snippet may hit or miss based on the LLM's phrasing.
- **No first-class sessions.** `DELETE /sessions/{id}` is a no-op.
- **No memory enumeration.** `GET /users/{id}/memories` returns an empty list. The KG is searchable via `/search` but not enumerable as discrete fact rows.
- **Synchronous cognify is slow.** Per-turn cognify = re-running KG enrichment over the dataset on each call. Expect ingestion to land in the 10–20 minute range for `fixtures/locomo-real` — well over the 5-minute budget. To benchmark cognee within budget, halve the fixture or run with batched cognify (out of scope here — would need a service-side change).
- **No off-topic gate.** Like mem0, cognee answers any query against its KG; off-topic queries get whatever the LLM produces. Adversarial probes will likely score lower than ours.

## Caveats with the adapter implementation

- **cognee API drift.** cognee is fast-moving; `SearchType` enum, `add()` signature, and `prune` API have all changed across recent versions. The adapter targets a recent stable API but may need tweaks if you upgrade `cognee` significantly.
- **Local storage.** cognee writes its data to `/data` inside the container, mounted via a docker volume. This persists across container restarts. To wipe, run `docker compose down -v`.
- **No embedding-dim Matryoshka equivalent for cognee's embedder config.** We pass `EMBEDDING_DIMENSIONS` env, but whether cognee respects it for the OpenAI embedder is version-dependent. If you see a vector-dim mismatch error in the logs (similar to what we hit on mem0), set `MEMORY_EMBED_MODEL=text-embedding-3-small` in `.env` so the default 1536-dim is used directly.

## What you'll see when you run it

Hypothesis (subject to cognee version variance):

- **Ingestion will be slow** (~10–20 min for the 6-session fixture) because of per-turn `cognify()` reprocessing. Cognee really wants batch ingestion.
- **Recall will be slow** (~5–10 sec per probe) because `GRAPH_COMPLETION` involves an LLM call.
- **Single-hop and multi-hop should be reasonable** because the KG captures relationships well — likely better than mem0 on multi-hop.
- **Adversarial will likely lose** because there's no off-topic gate; cognee will produce a completion for any query.
- **Temporal will likely lose** for the same reason as mem0 and our service: timestamps don't propagate well into extracted nodes.

The adapter exists to verify those hypotheses on the same fixture, not to declare a winner.
