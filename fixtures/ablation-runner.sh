#!/usr/bin/env bash
# Ablation runner — sweeps the named configs from docs/recall-configs.md
# against fixtures/locomo-real and writes per-category scores to a CSV.
#
# Strategy:
#   - Bind-mount the source appsettings.json into the memory-service container
#     so edits become live without rebuilding the image.
#   - Iterate configs: write new Recall:Stages + tunables, `docker compose
#     restart memory-service` (~5s), run probes via SKIP_INGEST=1 (~30s).
#   - First config does a full ingest; data persists in pgvector across configs.
#   - Restore the original appsettings.json on exit (trap).
#
# Usage:  fixtures/ablation-runner.sh
# Output: fixtures/ablation-results.csv

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APPSETTINGS="$REPO/src/MemoryService.Api/appsettings.json"
OVERRIDE="$REPO/docker-compose.ablation.override.yml"
RESULTS_CSV="$REPO/fixtures/ablation-results.csv"
ORIGINAL_BACKUP="$(mktemp -t ablation-appsettings.XXXXXX)"

# Capture original config so the trap can restore it.
cp "$APPSETTINGS" "$ORIGINAL_BACKUP"

cleanup() {
  echo ""
  echo "=== restoring original appsettings.json + tearing down override ==="
  cp "$ORIGINAL_BACKUP" "$APPSETTINGS"
  rm -f "$ORIGINAL_BACKUP" "$OVERRIDE"
  docker compose up -d memory-service >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

# ---- override file: bind-mount appsettings.json into the container ----
cat > "$OVERRIDE" <<EOF
services:
  memory-service:
    volumes:
      - $APPSETTINGS:/app/appsettings.json:ro
EOF

# ---- config catalogue (matches docs/recall-configs.md) ----
# Format:  name|stages_csv|tunables_json
declare -a CONFIGS=(
  'default|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'

  'no_query_rewrite|HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'no_floor|QueryRewrite,HybridRetrieval,MultiHopExpansion,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'no_multihop|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'no_low_score_filter|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'no_tier_a|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'no_tier_b|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierAStableFacts,TierCRecentSession,Assembly|{}'
  'no_tier_c|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,Assembly|{}'

  'floor_after_multihop|QueryRewrite,HybridRetrieval,MultiHopExpansion,VectorSimilarityFloor,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'filter_before_multihop|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,LowScoreFilter,MultiHopExpansion,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'
  'floor_last_in_shaping|QueryRewrite,HybridRetrieval,MultiHopExpansion,LowScoreFilter,VectorSimilarityFloor,TierAStableFacts,TierBRelevantMemories,TierCRecentSession,Assembly|{}'

  'recent_first|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierCRecentSession,TierAStableFacts,TierBRelevantMemories,Assembly|{}'
  'tier_a_only|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,TierAStableFacts,Assembly|{}'
  'tier_b_only|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,MultiHopExpansion,LowScoreFilter,TierBRelevantMemories,Assembly|{}'

  'baseline_inside|HybridRetrieval,TierAStableFacts,TierBRelevantMemories,Assembly|{}'
  'floor_only|HybridRetrieval,VectorSimilarityFloor,TierAStableFacts,TierBRelevantMemories,Assembly|{}'
  'multihop_only|HybridRetrieval,MultiHopExpansion,TierBRelevantMemories,Assembly|{}'
  'tier_a_with_floor|HybridRetrieval,VectorSimilarityFloor,TierAStableFacts,Assembly|{}'
  'degenerate|HybridRetrieval,Assembly|{}'

  'noisy_traffic|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,LowScoreFilter,TierAStableFacts,TierBRelevantMemories,Assembly|{"VectorSimilarityFloor":0.55}'
  'factual_lookup|HybridRetrieval,MultiHopExpansion,LowScoreFilter,TierBRelevantMemories,Assembly|{"MultiHopMaxHops":3}'
  'chat_with_recency|QueryRewrite,HybridRetrieval,VectorSimilarityFloor,TierAStableFacts,TierCRecentSession,Assembly|{"MaxRecentTurns":5}'
)

# ---- bring up the stack with the bind-mount override ----
echo "=== starting stack with appsettings.json bind-mounted ==="
docker compose -f "$REPO/docker-compose.yml" -f "$OVERRIDE" up -d memory-service postgres 2>&1 | tail -3
until curl -sf http://localhost:8080/health >/dev/null 2>&1; do sleep 1; done
echo "service ready"
echo ""

# ---- CSV header ----
echo "config,total,single_hop,multi_hop,temporal,open_domain,adversarial,wall_seconds" > "$RESULTS_CSV"

# ---- iterate ----
INGESTED=false
for entry in "${CONFIGS[@]}"; do
  name="${entry%%|*}"
  rest="${entry#*|}"
  stages="${rest%%|*}"
  tunables="${rest#*|}"

  echo "==================== $name ===================="

  # Write new appsettings.json. Always reset Recall to defaults, then apply
  # this config's stages list and tunable overrides.
  python3 - "$APPSETTINGS" "$stages" "$tunables" <<'PY'
import json, sys
path, stages_csv, tunables_json = sys.argv[1], sys.argv[2], sys.argv[3]
with open(path) as f:
    cfg = json.load(f)
defaults = {
    "PerSubqueryK": 30, "MultiHopMaxHops": 2, "MultiHopSeedTopK": 10,
    "MaxRecentTurns": 3, "MinKeepScore": 0.005,
    "VectorSimilarityFloor": 0.45, "MaxRankedTake": 40,
    "StableFactConfidence": 0.7, "StableFactsTopK": 20,
}
cfg["Recall"] = {**defaults, "Stages": stages_csv.split(",")}
overrides = json.loads(tunables_json)
for k, v in overrides.items():
    cfg["Recall"][k] = v
with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
PY

  # Restart service to pick up new config (~5s).
  # Pass the override flag explicitly so compose keeps the bind-mount when restarting.
  docker compose -f "$REPO/docker-compose.yml" -f "$OVERRIDE" restart memory-service >/dev/null 2>&1
  until curl -sf http://localhost:8080/health >/dev/null 2>&1; do sleep 2; done

  # Sanity check: confirm the container's file matches the host file (bind mount working).
  expected_first="${stages%%,*}"
  expected_last="${stages##*,}"
  container_text=$(docker exec higgsfield-test-task-memory-service-1 cat /app/appsettings.json 2>/dev/null || echo "")
  if ! echo "$container_text" | grep -q "\"$expected_first\"" || \
     ! echo "$container_text" | grep -q "\"$expected_last\""; then
    echo "  !! container appsettings.json missing expected Stages[$expected_first..$expected_last] — config didn't propagate"
    echo "$name,SKIPPED,SKIPPED,SKIPPED,SKIPPED,SKIPPED,SKIPPED,0" >> "$RESULTS_CSV"
    continue
  fi

  # Run the eval. First config does full ingest; subsequent reuse pgvector data.
  if [ "$INGESTED" = "false" ]; then
    SKIP=""
    INGESTED=true
  else
    SKIP="SKIP_INGEST=1"
  fi

  start=$(date +%s)
  if [ -n "$SKIP" ]; then
    output=$(SKIP_INGEST=1 "$REPO/fixtures/run-eval.sh" "$REPO/fixtures/locomo-real" 2>&1) || true
  else
    output=$("$REPO/fixtures/run-eval.sh" "$REPO/fixtures/locomo-real" 2>&1) || true
  fi
  end=$(date +%s)
  wall=$((end - start))

  # Parse per-category from the eval's tail output
  total=$(echo "$output" | grep -oE 'Summary: [0-9]+' | grep -oE '[0-9]+' | head -1)
  cat_single=$(echo "$output"   | grep -oE 'single-hop: [0-9]+'   | grep -oE '[0-9]+' | head -1)
  cat_multi=$(echo "$output"    | grep -oE 'multi-hop: [0-9]+'    | grep -oE '[0-9]+' | head -1)
  cat_temporal=$(echo "$output" | grep -oE 'temporal: [0-9]+'     | grep -oE '[0-9]+' | head -1)
  cat_open=$(echo "$output"     | grep -oE 'open-domain: [0-9]+'  | grep -oE '[0-9]+' | head -1)
  cat_adv=$(echo "$output"      | grep -oE 'adversarial: [0-9]+'  | grep -oE '[0-9]+' | head -1)

  total=${total:-0}
  cat_single=${cat_single:-0}
  cat_multi=${cat_multi:-0}
  cat_temporal=${cat_temporal:-0}
  cat_open=${cat_open:-0}
  cat_adv=${cat_adv:-0}

  echo "$name -> total=$total single=$cat_single multi=$cat_multi temporal=$cat_temporal open=$cat_open adv=$cat_adv  ${wall}s"
  echo "$name,$total,$cat_single,$cat_multi,$cat_temporal,$cat_open,$cat_adv,$wall" >> "$RESULTS_CSV"
done

echo ""
echo "=================== Done ==================="
echo "Results in $RESULTS_CSV"
echo ""
column -t -s , "$RESULTS_CSV"
