# LoCoMo (real) — 5-minute slice

A small slice of the actual [snap-research/LoCoMo](https://github.com/snap-research/locomo) `locomo10.json` dataset, sized to ingest + score in **under 5 minutes** against `gpt-4o-mini`.

## What this includes

- **Conversation** — one sample (`conv-26`) from `locomo10.json`, sessions 1 through 6. 108 LoCoMo dialogue messages, batched 2-at-a-time into 55 `/turns` calls.
- **Probes** — 23 QA pairs from the same sample whose `evidence` dia_ids fall in sessions 1–6, balanced across LoCoMo's 5 categories (capped at 5 per category):

  | Category id | LoCoMo name | Probes here |
  |---|---|---|
  | 4 | single-hop | 5 |
  | 1 | multi-hop | 5 |
  | 2 | temporal | 5 |
  | 3 | open-domain | 3 |
  | 5 | adversarial | 5 |

  (Open-domain has only 3 because most cat-3 questions require evidence beyond session 6.)

Sized to land near the 5-minute wall-time budget against `gpt-4o-mini` — typical run lands around 4:30–4:45.

## Build it

The converter depends on a local checkout of `snap-research/locomo`:

```bash
git clone --depth 1 https://github.com/snap-research/locomo.git /tmp/locomo
python3 fixtures/locomo-real/build.py             # uses /tmp/locomo/data/locomo10.json
# or:
python3 fixtures/locomo-real/build.py /path/to/locomo10.json
```

## Run it

```bash
docker compose up -d
until curl -sf http://localhost:8080/health; do sleep 1; done
fixtures/run-eval.sh fixtures/locomo-real
```

## Adaptations vs. raw LoCoMo

These choices are documented in `build.py` and are the reason this fits in 5 minutes / our `/recall` shape:

- **Single focal speaker.** LoCoMo conversations have two human speakers (here: Caroline + Melanie). Our extractor prompt is anchored on "the USER" (singular), so we pick **Caroline** as focal — her messages become `role=user`, Melanie's become `role=assistant`. QA pairs explicitly about Melanie (e.g. "When did Melanie paint a sunrise?") are dropped because Melanie's facts don't enter Caroline's memory store. To run a Melanie-focal eval you would re-build with `FOCAL_SPEAKER = "Melanie"` in `build.py`.
- **Substring grading**, not LoCoMo's F1. The official LoCoMo eval computes token-level F1 between the model's answer and the gold answer; that needs a judge LLM. Our shell-based runner does substring matching against the formatted `/recall` context — coarser but cheap and fast. Substrings are auto-derived from the gold answer in `build.py` (year tokens preferred, otherwise first content word ≥ 4 letters).
- **Adversarial → forbidden-substring.** LoCoMo cat 5 questions have an `adversarial_answer` field — the misleading wrong answer the system shouldn't surface. Our probe puts the key term from `adversarial_answer` in `expect_absent_substrings` so a context that contains it counts as a fail.
- **Sessions 1–6 only.** Real LoCoMo conversations span 19+ sessions of 15–40 messages each. Full ingestion would take hours. Six sessions of ~18 messages each is the sweet spot for a 5-minute test — change `INCLUDED_SESSIONS` in `build.py` to widen or narrow the slice.

## Expected baseline

Against `gpt-4o-mini` with our extraction pipeline, current numbers are roughly:

```
adversarial:  5/5  ✓  off-topic gate suppresses misleading retrievals reliably
single-hop:   2/5  ~  passes when the gold answer keyword survives extraction; fails on paraphrases
multi-hop:    1/5  ~  edge traversal works when subjects are crisply named; loses fidelity on "research" / "identity"
temporal:     1/5  ✗  exact dates from /turns timestamps don't make it into memory text
open-domain:  0/3  ✗  inferred-fact answers ("psychology") rely on agent reasoning, not on /recall
              ────
total:        9/23
```

The failures cluster around three real system gaps:

1. **Temporal precision.** Our memory `text` is the canonical natural-language form ("User attended an LGBTQ support group"); the original timestamp lives only on the `turn` row and is not surfaced in the context. To close: append `[YYYY-MM-DD]` to extracted memories, or include the source turn's timestamp next to each citation.
2. **Inferential answers.** Open-domain questions like "What fields would Caroline be likely to pursue in her education?" expect the *agent* to infer "psychology" from facts like "interested in mental health". `/recall` correctly returns the supporting facts; the substring grader is too strict because it expects the inferred word.
3. **Substring-match brittleness.** A real fix on (1) and (2) would still leave probes 06–08 (multi-hop "adoption", "transgender", "single") flickering because the LLM extractor paraphrases — e.g. produces "adoption agencies" but the substring is "adoption" (passes), or produces "trans woman" while the substring is "transgender" (fails). Moving to LLM-judge grading would solve this — out of scope for a 5-minute shell-driven test.

These are kept as failing probes for honest signal. They convert to passes when the underlying gap closes without changes to the fixture.

## What's NOT measured here

- **F1 scoring** — our substring grader is coarser than LoCoMo's F1. A full-faithfulness run needs an LLM judge.
- **Cross-session reasoning beyond session 2** — those QAs are dropped because we don't ingest later sessions.
- **Multi-modal grounding** — LoCoMo includes some image-grounded QAs. The `dia_id`-based filter implicitly excludes them since their evidence often spans image-bearing turns we can't process anyway.
- **Both-speaker QA** — see "single focal speaker" above. To test attribution to two distinct subjects, you'd need a second user_id and second build pass.

## Promoting this to a fuller LoCoMo run

The harness already takes any directory with `conversations/` + `probes/`, so scaling up is purely a `build.py` change:

- Increase `INCLUDED_SESSIONS` to all sessions (cost: ~$0.50 per conversation, ~30 min ingestion).
- Set `MAX_PROBES = 199` (or whatever) and remove the per-category cap.
- Repeat per `SAMPLE_INDEX` 0..9 to cover all 10 conversations in `locomo10.json`.

A full run is ~10 conversations × ~2k messages × ~$0.001 per turn ≈ **$15–25 and 4–8 hours** as I estimated previously. The 7-probe slice here is the cheap iteration loop.
