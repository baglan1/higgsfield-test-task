# Vanilla baseline adapter

The dumbest possible "memory service" — embed every turn verbatim into pgvector, retrieve cosine top-k on `/recall`, format as a list. No extraction, no judge, no graph, no tier-budget, no off-topic gate, no hybrid retrieval, no anything.

Purpose: establish a control. None of the other adapters' scores mean anything in absolute terms until you know what plain "shove messages into a vector store" gets you on the same fixture. If vanilla scores 8/23 against your service's 9/23, the smart stuff is barely earning its keep. If vanilla scores 4/23, the architecture is doing real work.

## Run

```bash
docker compose -f adapters/baseline/docker-compose.yml --env-file .env up -d --build
until curl -sf http://localhost:8085/health; do sleep 1; done
BASE=http://localhost:8085 fixtures/run-eval.sh fixtures/locomo-real
```

Port **8085**. Own Postgres+pgvector instance (volume `baseline-pg`). Doesn't share state with the .NET service — clean comparison.

## What's implemented (the entire algorithm)

```python
# /turns
text = "\n".join(f"{m.role}: {m.content}" for m in messages)
vec  = openai.embeddings.create(input=text, model="text-embedding-3-small", dimensions=1536)
INSERT INTO messages (id, user_id, session_id, content, embedding) VALUES (...)

# /recall
qvec = openai.embeddings.create(input=query, ...)
SELECT id, content FROM messages
  WHERE user_id = ? ORDER BY embedding <=> qvec LIMIT 10
context = "## Known facts about this user\n" + "\n".join(f"- {c}" for c in contents)
```

That is it. ~150 lines including the schema bootstrap and FastAPI plumbing.

## Expected score profile

Hypothesis (will be confirmed by running):

- **single-hop**: should be the strongest category — direct factual queries match well to direct memory text.
- **multi-hop**: no graph, no edges. Probably 0–1/5. The Biscuit→Berlin probe will likely fail because the embedding for "city of dog-owner" doesn't pull both relevant turns up to the top.
- **temporal**: no date extraction, no edge timestamps. Year keywords in text-of-turn (e.g. "in 2023") will sometimes survive into context. Probably 1–2/5.
- **open-domain**: likely 0/3 — inferential answers can't come from raw retrieval alone.
- **adversarial**: 0/5. No off-topic gate; cosine top-k always returns *something* and the substring grader catches the forbidden token. This is the same pattern as mem0.
- **Wall time**: fast. ~30 ms/turn for embedding+insert, ~50 ms per recall. Full fixture should finish in **30–60 seconds** — easily inside the 5-minute budget.

If those hold, the comparison story becomes clearer: cognee's 15 and our 9 are both above the baseline, but the *gap* between them and vanilla is what actually measures architectural value.

## Caveats

- **No hybrid retrieval.** Pure cosine, no tsvector / BM25. Adding hybrid would shift this from "vanilla" toward "minimum smart" — out of scope for this slice.
- **No conversational context.** Each turn is one document. The retriever sees the user's "I work at Stripe" turn and the assistant's "got it" reply as one blob, embedded as one vector. Information is in there but not as structured memories.
- **Embedding dim Matryoshka** for `text-embedding-3-large` users: passes `dimensions=1536` to OpenAI so the column-type matches.
- **One Postgres per adapter.** Doesn't share with our .NET service. By design — the comparison should be on grading, not on whether the systems share infra.
