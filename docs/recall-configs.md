# Recall Pipeline Configurations

A catalogue of practical [`RecallPipelineOptions.Stages`](../src/MemoryService.Recall/RecallPipelineOptions.cs) configurations. Each one is a copy-pasteable JSON array that goes under the `Recall:Stages` key in [`appsettings.json`](../src/MemoryService.Api/appsettings.json) (or wherever you override it).

## How to apply a config

Three options, in order of how invasive they are:

**1. Edit `appsettings.json` directly.** Replace the `Recall:Stages` array. Restart the service.

**2. Per-environment override.** Create `appsettings.Production.json` (or `appsettings.Ablation.json`, etc.) with just the `Recall` section. Set `ASPNETCORE_ENVIRONMENT` accordingly. Cleaner than editing the base file.

**3. Environment variables (best for ad-hoc / scripted ablations).** ASP.NET Core binds env vars with `__` as the section separator and indices for arrays:

```bash
docker compose up -d \
  -e Recall__Stages__0=HybridRetrieval \
  -e Recall__Stages__1=TierAStableFacts \
  -e Recall__Stages__2=TierBRelevantMemories \
  -e Recall__Stages__3=Assembly
```

Tunables work the same way: `Recall__VectorSimilarityFloor=0.55`.

## Stage reference (one-line summary)

| Stage | Reads | Writes | Purpose |
|---|---|---|---|
| `QueryRewrite` | `Query.Query` | `SubQueries` | gated LLM expansion of short / pronoun-heavy queries |
| `HybridRetrieval` | `SubQueries` (or raw query) | `Combined`, `MaxVectorSimilarity` | per-subquery vector + lexical, RRF-fused |
| `VectorSimilarityFloor` | `MaxVectorSimilarity` | terminates if below floor | off-topic gate; the only adversarial-resistance mechanism |
| `MultiHopExpansion` | `Combined` | `Combined` (augmented) | 2-hop BFS over `memory_edges` from top-K seeds |
| `LowScoreFilter` | `Combined` | `Ranked` | drops below `MinKeepScore`, caps to `MaxRankedTake` |
| `TierAStableFacts` | DB | `StableFacts` | always-include user facts above confidence threshold |
| `TierBRelevantMemories` | `Ranked`, `StableFacts` | `Relevant` | query-relevant memories, excluding Tier A; loads its own rows |
| `TierCRecentSession` | DB | `RecentTurns` | last N turns from same session |
| `Assembly` | all of the above | `Result` | tier-budget assembly under `MaxTokens`; emits final `RecallResult` |

Hard ordering constraint: `TierAStableFacts` must precede `TierBRelevantMemories`. Everything else is freely orderable as long as data dependencies are respected (see [the constraint table in the discussion](../README.md) for details).

---

## 1. Defaults

### `default` — the shipping configuration

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Use for production traffic. On `fixtures/locomo-real`: 9/23, 4:37 wall. Adversarial 5/5 (the unique strength), single-hop 2/5 (the substring-grader weakness).

---

## 2. Single-stage ablations

Each row drops one optional stage from the default. Useful for "what does this stage actually buy us?".

### `no_query_rewrite`

```json
[
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Tests whether sub-query expansion meaningfully helps. `HybridRetrieval` falls back to using `RecallQuery.Query` directly. Saves one LLM call per recall (the rewriter call is gated to ~30% of queries by default). Hypothesis: small score drop on multi-hop / open-domain probes that benefit from query decomposition; flat elsewhere.

### `no_floor` — disable adversarial gate

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Tests how much of our adversarial score is owed to `VectorSimilarityFloor`. Hypothesis: adversarial 5/5 → 0–1/5. Other categories unchanged. This is the **single most impactful ablation** for the design narrative.

### `no_multihop`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Tests how much the `memory_edges` graph contributes. Hypothesis: multi-hop probes drop from current 1/5 toward 0/5; other categories flat.

### `no_low_score_filter`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Tests whether the noise-floor is doing useful work once `VectorSimilarityFloor` is in place. With LSF off, `TierBRelevantMemories` falls back to `Combined` instead of `Ranked`. Hypothesis: minor changes; LSF and VSF overlap in purpose.

### `no_tier_a` — kill stable-fact baseline

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Tests whether always-include-stable-facts boosts recall or just dilutes the context. Hypothesis: small drop on probes whose answer is a stable fact (e.g. employment, location); other categories flat.

### `no_tier_b` — only baseline + recent

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierCRecentSession",
  "Assembly"
]
```

Removes query-driven memories; only Tier A baseline + Tier C recency surface. Mostly a sanity check for "is Tier B doing the heavy lifting?". Hypothesis: significant drop in single-hop / multi-hop; adversarial unchanged.

### `no_tier_c`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "Assembly"
]
```

Tests whether the recent-session window contributes anything on a fixture where the probe session isn't the same as the data session. Hypothesis: zero impact on locomo-real (probes use a different session_id); would matter on real production traffic.

---

## 3. Reorderings (signal-shaping phase)

Same set of stages as default; different positions in the post-retrieval section.

### `floor_after_multihop`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "MultiHopExpansion",
  "VectorSimilarityFloor",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Floor gates *after* the graph adds neighbors. Useful when you want multi-hop to potentially rescue an off-topic-looking query (e.g. the seed memory has low cosine but its 2-hop neighbor matches well). Hypothesis: slightly lower adversarial (some weak similarities now survive multi-hop); slightly higher multi-hop on edge cases.

### `filter_before_multihop`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "LowScoreFilter",
  "MultiHopExpansion",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Filter narrows to top-K *before* multi-hop seeds. Faster (fewer seeds → fewer edge lookups) but loses some neighbors if the filter dropped a useful low-score seed. Hypothesis: 5–10% faster wall time; slight multi-hop drop.

### `floor_last_in_shaping`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "MultiHopExpansion",
  "LowScoreFilter",
  "VectorSimilarityFloor",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "TierCRecentSession",
  "Assembly"
]
```

Floor is the very last gate before tier construction. Sees the fully-shaped candidate set. Most permissive in terms of letting data through to the floor decision. Hypothesis: same adversarial as default; possibly higher recall on edge cases.

---

## 4. Reorderings (tier-construction phase)

### `recent_first`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierCRecentSession",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "Assembly"
]
```

Tier C populated first. Doesn't change the Assembler's output ordering (the tier-budget assembler reads tiers by name regardless of pipeline position), but does reorder log lines and could matter if a future stage reads `RecentTurns` before A/B fill. Mostly diagnostic.

### `tier_a_only`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "TierAStableFacts",
  "Assembly"
]
```

Only stable facts surface. Ignores the query content entirely (Tier A doesn't use `Combined`). Useful as an "always-on memory" baseline — interesting to compare against Tier B's query-driven approach.

### `tier_b_only`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierBRelevantMemories",
  "Assembly"
]
```

Only query-relevant memories. No baseline of stable facts. Hypothesis: similar single-hop / multi-hop to default; lower on probes whose answer is a stable fact the query didn't strongly retrieve.

---

## 5. Aggressive ablations

These are deliberately stripped-down configs that test specific claims about what's contributing what.

### `baseline_inside`

```json
[
  "HybridRetrieval",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "Assembly"
]
```

The simplest pipeline that produces non-empty output. No rewrite, no floor, no multi-hop, no filtering. Tier B falls back to using `Combined` (since `Ranked` is empty). Should approximate the [vanilla baseline adapter](../adapters/baseline/) result of 9/23. If our internal "minimum smart" matches the standalone baseline, it confirms the harness is consistent. If it differs significantly, there's a confound to investigate.

### `floor_only`

```json
[
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "Assembly"
]
```

The floor is the only piece of "smart" machinery. Tests the strongest claim: is `VectorSimilarityFloor` alone enough to get our adversarial gap, or does it need the rest of the pipeline supporting it? Hypothesis: adversarial 5/5; everything else similar to `baseline_inside`.

### `multihop_only`

```json
[
  "HybridRetrieval",
  "MultiHopExpansion",
  "TierBRelevantMemories",
  "Assembly"
]
```

No rewrite, no floor, no filter, no Tier A. Pure retrieval + graph. Tests whether the graph alone meaningfully beats vanilla on multi-hop. Hypothesis: multi-hop probes 2–3/5 (up from baseline 3/5? or actually equal — the substring grader is forgiving on multi-hop in our fixture).

### `tier_a_with_floor`

```json
[
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "TierAStableFacts",
  "Assembly"
]
```

"Stable facts but only when the query is on-topic." Tier B disabled. Combines the adversarial-resistance of the floor with the always-there-ness of Tier A. Useful for testing "just give me the user's profile when relevant".

### `degenerate`

```json
[
  "HybridRetrieval",
  "Assembly"
]
```

Nothing surfaces because Tier A/B/C are all off and `Assembly` has no Tier inputs. Should produce empty context for every probe. Sanity check that the pipeline runner doesn't choke on a near-empty config.

---

## 6. Production-tuning suggestions

Configs you might actually deploy depending on your traffic mix.

### `noisy_traffic`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "LowScoreFilter",
  "TierAStableFacts",
  "TierBRelevantMemories",
  "Assembly"
]
```

With `VectorSimilarityFloor: 0.55` (stricter than 0.45 default). Drops multi-hop and Tier C. Use when most traffic is exploratory ("hi", "what's up", "how are you") and you want aggressive abstention to avoid hallucinated context.

### `factual_lookup`

```json
[
  "HybridRetrieval",
  "MultiHopExpansion",
  "LowScoreFilter",
  "TierBRelevantMemories",
  "Assembly"
]
```

With `MultiHopMaxHops: 3` for deeper graph walks. Drops the floor and Tier A. Use when traffic is structured fact-lookup and the agent is OK with seeing weak matches.

### `chat_with_recency`

```json
[
  "QueryRewrite",
  "HybridRetrieval",
  "VectorSimilarityFloor",
  "TierAStableFacts",
  "TierCRecentSession",
  "Assembly"
]
```

With `MaxRecentTurns: 5`. Drops graph traversal and query-relevant memories; favours stable facts + recent context. Use for assistant chats where the agent already has the recent conversation in its prompt and just needs a baseline of who the user is.

---

## 7. How to compare these in practice

A small ablation runner (not yet built) would:

1. Enumerate the configs in this file (e.g. parse a `configs.yaml`).
2. For each: write a temp `appsettings.json`, restart the service, run `fixtures/run-eval.sh fixtures/locomo-real`, capture per-category scores.
3. Output a CSV: rows = config name, columns = per-category scores + wall time.

Approximate budget: 10 configs × 4:30 each ≈ **45 minutes**, ~$0.50 OpenAI cost. Worth running once to validate the hypotheses above, then again whenever the pipeline is meaningfully changed.

If the ablation runner doesn't exist yet but you want to compare two configs by hand, the workflow is:

```bash
# config A
docker compose down memory-service
# edit appsettings.json
docker compose up -d --build memory-service
fixtures/run-eval.sh fixtures/locomo-real | tee /tmp/config-a.log

# config B
docker compose down memory-service
# edit appsettings.json again
docker compose up -d --build memory-service
fixtures/run-eval.sh fixtures/locomo-real | tee /tmp/config-b.log

diff <(grep -E '\[(PASS|FAIL)\]' /tmp/config-a.log) <(grep -E '\[(PASS|FAIL)\]' /tmp/config-b.log)
```

The `diff` shows exactly which probes flipped between configs.
