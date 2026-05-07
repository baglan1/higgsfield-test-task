# mem0 adapter

A thin FastAPI service that wraps [mem0ai](https://github.com/mem0ai/mem0) behind the same HTTP contract our .NET service exposes (`/turns`, `/recall`, `/search`, `/users/{id}/memories`, `DELETE /sessions/{id}`, `DELETE /users/{id}`, `/health`).

Purpose: run [`fixtures/run-eval.sh`](../../fixtures/run-eval.sh) against mem0 and our service with **the same probes**, **the same grading**, and **the same wall-time budget**. Apples-to-apples comparison.

## Layout

```
adapters/mem0/
├── main.py             # FastAPI adapter — translates our contract to mem0 calls
├── Dockerfile          # Python 3.12-slim image
├── docker-compose.yml  # qdrant (vector store) + mem0-adapter
├── requirements.txt
└── README.md           # this file
```

The adapter is its own compose project. It does NOT share Postgres with our service — mem0's recommended store is Qdrant, so that's what's used here.

## Run

```bash
# from repo root
cd adapters/mem0
docker compose --env-file ../../.env up -d
until curl -sf http://localhost:8081/health; do sleep 1; done

# point the eval at it
BASE=http://localhost:8081 ../../fixtures/run-eval.sh ../../fixtures/locomo-real
```

The `--env-file` flag reuses the same `.env` you use for the main service (so the same `OPENAI_API_KEY` and `MEMORY_AUTH_TOKEN` apply to both).

To compare side-by-side:

```bash
# main service on 8080 (in repo root)
docker compose up -d
fixtures/run-eval.sh fixtures/locomo-real | tee /tmp/our-service.log

# mem0 adapter on 8081 (in adapters/mem0/)
docker compose --env-file ../../.env up -d
BASE=http://localhost:8081 ../../fixtures/run-eval.sh ../../fixtures/locomo-real | tee /tmp/mem0.log

diff <(grep -E '\[(PASS|FAIL)\]' /tmp/our-service.log) <(grep -E '\[(PASS|FAIL)\]' /tmp/mem0.log)
```

## Mismatches that affect interpretation

mem0 and our service are not isomorphic. The adapter does what it can, but a few things bend the comparison:

- **No first-class sessions.** mem0 stores everything as memories under a `user_id`; sessions are encoded as metadata. `DELETE /sessions/{id}` is a best-effort metadata-filter delete and may silently succeed without removing anything if the installed mem0 version doesn't support filter-based delete.
- **No multi-hop edges.** mem0 has no equivalent of our `memory_edges` table or 2-hop traversal. The probe `multi_hop_pet_city` ("which city does the user with the dog Biscuit live in?") will pass for mem0 only if vector similarity alone connects "Biscuit" memory and "Berlin" memory — not via graph traversal.
- **No tier-budgeted assembly.** mem0 returns flat top-k. Our adapter formats them as a single "## Known facts about this user" block. There's no Tier-A / Tier-B / Tier-C split, no `max_tokens` budget enforcement (mem0 doesn't expose one).
- **No vector-similarity floor.** Our service explicitly suppresses off-topic queries via a similarity threshold; mem0 always returns its top-k. Adversarial probes that pass for our service via the floor may fail for mem0 simply because mem0 returns *something* even when nothing is relevant.
- **No aspect/stance / opinion arcs.** mem0's update logic is "memory text similarity" — it may overwrite an opinion when our service would have COEXIST'd it. Probes about evolving views may give different answers.
- **Different LLM call shape.** mem0 issues 1–2 LLM calls per `add()` (its own extractor + judge). Our service typically issues 3–5 (extractor + per-candidate judge). Cost per turn will differ.

These are not bugs in the adapter — they're real differences in what each system does. They mean a probe pass/fail on mem0 reflects mem0's design + our adapter's interpretation, not just mem0's "memory quality" in isolation.

## Caveats with the adapter implementation

- **mem0 API drift.** mem0 has changed `add()` and `search()` return shapes between versions. The adapter handles both `{"results": [...]}` and bare-list returns via `_results_list`. If you upgrade `mem0ai` and see weird empty responses, check the raw return shape against `_results_list`.
- **No streaming / async.** FastAPI routes are sync because mem0's SDK is sync. For a benchmark run this is fine; for production load you'd want a thread pool.
- **No `infer=False` escape.** The adapter always lets mem0 do its own extraction. To compare "what if we skipped extraction" you'd add a flag — out of scope for v1.

## What you'll see when you run

Expected rough shape (subject to mem0 version + your fixture set):

- mem0 ingestion will be **faster** than ours per turn (fewer LLM calls — no separate judge step on candidates).
- mem0 will likely **lose** on the multi-hop probe and the adversarial probes; **win** on simple single-hop in some cases (less aggressive de-duplication = more raw memories surfaced).
- Total wall time on `fixtures/locomo-real` should land around **2:30–3:30** vs. our ~4:37.

The point of the adapter isn't to declare a winner; it's to make the trade-offs visible on the same fixtures.
