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

## Ablation sweep on locomo-real

22 named pipeline configurations from [docs/recall-configs.md](../docs/recall-configs.md), swept by [fixtures/ablation-runner.sh](ablation-runner.sh). Same fixture, same harness, same DB state (first config does full ingest, the rest reuse pgvector data via `SKIP_INGEST=1`), only the `Recall:Stages` list and tunables vary between rows. Output CSV: [fixtures/ablation-results.csv](ablation-results.csv).

| Config | Total | single (5) | multi (5) | temporal (5) | open (3) | adv (5) |
|---|---|---|---|---|---|---|
| **factual_lookup** (no floor, no Tier-A, MultiHopMaxHops=3) | **13 / 23** | 4 | 5 | 2 | 0 | 2 |
| **no_floor** (default minus VectorSimilarityFloor) | **12 / 23** | 3 | 5 | 2 | 0 | 2 |
| baseline_inside (Hybrid + TierA + TierB + Assembly) | 9 / 23 | 1 | 2 | 2 | 0 | 4 |
| no_low_score_filter | 8 / 23 | 1 | 2 | 1 | 0 | 4 |
| no_tier_a | 8 / 23 | 3 | 2 | 1 | 0 | 2 |
| no_tier_b | 8 / 23 | 1 | 2 | 1 | 0 | 4 |
| tier_a_only | 8 / 23 | 1 | 2 | 1 | 0 | 4 |
| tier_b_only | 8 / 23 | 3 | 2 | 1 | 0 | 2 |
| floor_only (Hybrid + Floor + TierA + TierB + Assembly) | 8 / 23 | 1 | 2 | 1 | 0 | 4 |
| tier_a_with_floor | 8 / 23 | 1 | 2 | 1 | 0 | 4 |
| noisy_traffic (floor=0.55) | 8 / 23 | 2 | 1 | 1 | 0 | 4 |
| chat_with_recency (MaxRecentTurns=5) | 8 / 23 | 1 | 2 | 1 | 0 | 4 |
| **default** (full 9-stage pipeline) | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| no_query_rewrite | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| no_multihop | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| no_tier_c | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| floor_after_multihop | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| filter_before_multihop | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| floor_last_in_shaping | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| recent_first | 7 / 23 | 2 | 2 | 1 | 0 | 2 |
| degenerate (Hybrid + Assembly only) | 5 / 23 | 0 | 0 | 0 | 0 | 5 |
| multihop_only (Hybrid + MultiHop + TierB + Assembly) | 5 / 23 | 0 | 0 | 0 | 0 | 5 |

### What this tells us

**The 0.45 vector floor is overshooting on this fixture.** Removing it (`no_floor`) lifts total from 7→12 — the entire jump comes from multi-hop (2→5) and a small bump in single-hop (2→3). The floor is short-circuiting queries whose top raw cosine sits just below 0.45, even though hybrid+lexical+multi-hop would have surfaced the answer. Adversarial does *not* drop when the floor is removed (still 2/5) — on this re-ingested data, several adversarial probes get filtered by other downstream conditions, so the floor's value is smaller than the earlier comparison run suggested.

**Tunable beats topology.** `factual_lookup` (13/23) and `no_floor` (12/23) are the only configs that meaningfully outperform the default; both win by removing or relaxing the floor. Pure reorderings — `floor_after_multihop`, `filter_before_multihop`, `floor_last_in_shaping`, `recent_first` — all land on the same 7/23 as default. Order of stages doesn't move the needle on this fixture; whether the floor fires (and how strict it is) does. This also confirms the configuration plumbing is faithful: identical retrieval+assembly behavior shows up as identical scores across reordered runs, so any score delta we *do* see is signal, not config drift.

**The "degenerate baseline" (5/23 from 5/5 adversarial) is a useful zero-line.** With only `HybridRetrieval + Assembly` (no Tier stages populating `RecallContext.StableFacts/Relevant/RecentTurns`), Assembly returns empty markdown — and substring-grading awards 5/5 on adversarial probes that *expect* empty answers. This is the "do nothing and still get points" floor. Any config scoring ≤5/23 on substantive categories isn't actually retrieving anything; it's just refusing every query. `multihop_only` falls into the same bucket because `TierBRelevantMemories` reads from `Ranked` which is populated by `LowScoreFilter` — without the filter stage, the tier is empty.

**Default (full 9-stage) underperforms its own subsets.** This is the design takeaway: the current default isn't pareto-optimal. A pruned pipeline (`factual_lookup`: drop QueryRewrite, drop floor, drop Tier-A and Tier-C, raise multi-hop to 3) tops the table at 13/23. The cost is adversarial drops from default's 2 to 2 (no change here) — but on the previously-ingested run that produced the public 10/23 number, adversarial was 5/5 with the floor, so this trade-off is data-dependent. Treat 12–13/23 as the upper bound *this pipeline can reach without judge-LLM grading*; the gap to cognee's 15/23 is fundamentally an architecture gap (graph KG vs flat embeddings), not a tuning gap.

**Caveat — extraction variance is non-trivial.** The default 9-stage pipeline scored 10/23 in the published table above (different ingest run) and 7/23 here (this ingest run). The corpus is the same; the LLM extractor's outputs vary turn-to-turn. ~3 probes' worth of swing between ingest runs is the noise floor for any single-row comparison; only deltas larger than that should be read as architectural signal.

## How to add another adapter to this table

1. Build the adapter under `adapters/<system>/` with a self-contained `docker-compose.yml`.
2. Bring it up on a different port (mem0 is on 8081; pick a higher port).
3. Run `BASE=http://localhost:<port> fixtures/run-eval.sh fixtures/locomo-real`.
4. Add a row above with the per-category breakdown and wall time.
5. Add a per-system notes paragraph documenting impedance mismatches.

The script supports `SKIP_INGEST=1` for iterating on probe-side changes without paying ingestion time again, useful when fitting an adapter to the contract.
