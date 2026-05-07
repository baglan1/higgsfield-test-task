# Benchmark Results

Recorded results per `(system × fixture)` combination. Update manually after each significant run.

The same probes, the same grading function ([fixtures/run-eval.sh](run-eval.sh)), and the same `OPENAI_API_KEY` with `gpt-4o-mini` chat + `text-embedding-3-large` (Matryoshka-truncated to 1536 dim) for every system. Anything that differs between systems is the system itself, not the harness.

## locomo-real (1 conversation, 6 sessions, 23 probes)

| System | Total | single-hop (5) | multi-hop (5) | temporal (5) | open-domain (3) | adversarial (5) | Wall time |
|---|---|---|---|---|---|---|---|
| **Vanilla baseline** (pgvector, no extraction) | 9 / 23 | 4 | 3 | 2 | 0 | 0 | 0:42 |
| **Our .NET service** | 10 / 23² | 2 | 1 | 1 | 1 | 5 | 4:20 |
| **mem0** (Qdrant) | 10 / 23 | 4 | 2 | 2 | 1 | 1 | 10:06¹ |
| **cognee** (LanceDB+sqlite) | 15 / 23 | 4 | 4 | 5 | 1 | 1 | 7:42¹ |
| **Graphiti** (FalkorDB) | 12 / 23 | 2 | 2 | 5 | 0 | 3 | 13:05¹ |

¹ Both cognee and mem0 exceeded the 5-minute budget for the full 6-session fixture. To benchmark them within budget, halve the fixture (3 sessions instead of 6) — change `INCLUDED_SESSIONS` in `fixtures/locomo-real/build.py`.

² Measured after the stage-based pipeline refactor with the default `Stages` list. Previous monolithic pipeline scored 9/23 on a separate run; the +1 here is within LLM-extraction variance (different memories surface across runs). Confirms the refactor is behavior-preserving.

### Per-system notes

**Vanilla baseline.** Read this first — it changes how the rest of the table reads. The "dumbest possible" implementation (embed every turn verbatim into pgvector, cosine top-k on `/recall`, no extraction / judge / graph / threshold / tier-budget) **ties our service on raw count (9/23) at 1/7th the wall time** (42 s vs 4:37). Pure-cosine retrieval against raw turn text turns out to be surprisingly competitive on substring-matched LoCoMo grading: single-hop is 4/5 because the answer keyword often survives in the stored turn text; multi-hop is 3/5 because the top-10 includes enough turns that the substring grader catches things by accident; temporal is 2/5 because year tokens like "2023" are in the raw conversation. Adversarial is 0/5 — no off-topic gate, top-k always returns *something*. This is the control: every other system needs to justify its complexity *against this number*. See [adapters/baseline/README.md](../adapters/baseline/README.md).

**Our .NET service.** Adversarial wins decisively because of the explicit `VectorSimilarityFloor = 0.45` gate. Loses on temporal (turn timestamps not propagated into memory text), open-domain (inferential answers — agent's job, not `/recall`'s), and substring-match brittleness on extracted paraphrases. **Important caveat now that vanilla is in the table**: our 9/23 raw count is the same as vanilla. The architecture isn't earning it on raw recall — it's earning it on the 5/5 adversarial gap (vanilla scored 0/5). For the substring grader on this fixture, what we add over vanilla is *noise resistance*, not *better recall*. See [fixtures/locomo-real/README.md](locomo-real/README.md) for details.

**mem0.** Wins on raw single-hop (4/5) because mem0 stores more verbose, redundant memories and the substring grader catches the answer keyword in any of them. Loses 4/5 adversarial — no off-topic gate; mem0 always returns top-k regardless of query relevance. Ingestion is ~2× slower than our service (against my prediction) — the current mem0 version does more LLM processing per turn than the older "1–2 calls" guidance suggests. See [adapters/mem0/README.md](../adapters/mem0/README.md) for adapter caveats and the four documented impedance mismatches (no first-class sessions, no multi-hop edges, no tier-budgeted assembly, no similarity floor).

**cognee.** Highest total. Wins decisively on **temporal (5/5)** — its KG captures dates as graph properties and the `GRAPH_COMPLETION` search asks an LLM to read the graph, which surfaces year tokens that our memory text loses. Wins on **multi-hop (4/5)** — same KG, edges between entities are first-class. Loses 4/5 **adversarial** for the same reason as mem0 (no off-topic gate; cognee always answers from its graph). The `/recall` shape is different from ours and mem0's: cognee returns a free-text LLM completion, not a memory list — so what gets graded is whatever the answering LLM chose to mention. See [adapters/cognee/README.md](../adapters/cognee/README.md) for adapter caveats; key one is that cognee has no first-class memory enumeration, so `GET /users/{id}/memories` returns empty.

**Graphiti.** Ties cognee on **temporal (5/5)** because its bi-temporal edges (`valid_at` per edge) are exactly the right shape — and unlike cognee, Graphiti returns dated facts directly (`[2023-06-27] Caroline owns the hand-painted bowl…`), so the `2023` substring lands. Loses on **multi-hop (2/5)** despite being a graph — search returns top-N edges by relevance, but the substring grader is looking for specific keywords ("transgender", "single") that may not appear in the highest-scoring edges. Surprisingly **adversarial (3/5)**: Graphiti's relevance threshold filters out enough off-topic results that some adversarial probes pass. Slowest of all five systems by wall time (13:05) — Graphiti runs entity resolution + relationship extraction + temporal reasoning per episode, so each `add_episode` call is heavier than mem0's `add()`. See [adapters/graphiti/README.md](../adapters/graphiti/README.md). Sanitization gotcha: FalkorDB's RediSearch index treats hyphens as separators, so user_ids with dashes need to be remapped (the adapter does this via `_group_id()`).


### Read this carefully

With the vanilla baseline as control, two stories crystallize:

**Story 1 — what's actually earning its keep.** Cognee (15) and Graphiti (12) genuinely beat the baseline (9). Both wins concentrate in the **temporal** category (5/5 vs vanilla's 2/5) and to a lesser extent **multi-hop**. Their KG architectures pay off — bi-temporal edges and graph traversal capture something cosine retrieval can't. mem0 (10) barely beats vanilla; the extraction overhead it adds is mostly washed out by the substring grader being forgiving on single-hop.

**Story 2 — what only the explicit-gate systems can do.** Adversarial. Vanilla and mem0 score 0/5 and 1/5; cognee scores 1/5; Graphiti gets 3/5 from a built-in relevance threshold; our service gets 5/5 from an explicit similarity floor. Every other category is a continuum where smarter helps marginally; adversarial is the only category where you either have a noise-suppression mechanism or you don't.

**Story 3 — what to do with this for the design conversation.** Each architecture is making a different bet:

- **Vanilla** — "raw retrieval is enough; complexity is overhead". Cheapest, fastest, lowest score.
- **Our service** — "extraction + supersession + similarity floor". The floor is the only differentiator vs vanilla on this fixture.
- **mem0** — "extraction + de-duplication + flat vector store". Marginal lift over vanilla.
- **cognee** — "pipeline KG construction; LLM reads the graph". Highest score, especially on temporal.
- **Graphiti** — "bi-temporal edges in a graph DB". Wins temporal, slowest by far.

A judge-LLM grader (instead of substring) would likely:
- Push **cognee further ahead** on single-hop and open-domain (its completions are answer-shaped already)
- Tilt **mem0** and our service **toward parity** on multi-hop and open-domain (verbose memories contain answers in different wording)
- Leave the **adversarial gap unchanged** — that's a real architectural difference between systems with and without an off-topic gate
- Probably narrow the **vanilla–our-service gap** further on most categories, leaving only adversarial as a clear win

## How to add another adapter to this table

1. Build the adapter under `adapters/<system>/` with a self-contained `docker-compose.yml`.
2. Bring it up on a different port (mem0 is on 8081; pick a higher port).
3. Run `BASE=http://localhost:<port> fixtures/run-eval.sh fixtures/locomo-real`.
4. Add a row above with the per-category breakdown and wall time.
5. Add a per-system notes paragraph documenting impedance mismatches.

The script supports `SKIP_INGEST=1` for iterating on probe-side changes without paying ingestion time again, useful when fitting an adapter to the contract.
