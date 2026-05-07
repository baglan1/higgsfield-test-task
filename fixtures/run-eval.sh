#!/usr/bin/env bash
# Recall-quality fixture runner.
# Ingests fixtures/conversations/*.json, then runs fixtures/probes/*.json
# against /recall and grades each probe against expected/absent substrings.
#
# Usage:  fixtures/run-eval.sh   (defaults to http://localhost:8080)
#         BASE=http://host:port fixtures/run-eval.sh

set -euo pipefail

BASE="${BASE:-http://localhost:8080}"
ROOT="$(cd "$(dirname "$0")" && pwd)"

# Pick up MEMORY_AUTH_TOKEN from current env, or from .env in repo root.
if [[ -z "${MEMORY_AUTH_TOKEN:-}" && -f "$ROOT/../.env" ]]; then
  set +u
  # shellcheck disable=SC1090
  MEMORY_AUTH_TOKEN=$(grep -E '^MEMORY_AUTH_TOKEN=' "$ROOT/../.env" | head -1 | cut -d= -f2-)
  set -u
fi
AUTH_HEADER=()
if [[ -n "${MEMORY_AUTH_TOKEN:-}" ]]; then
  AUTH_HEADER=(-H "Authorization: Bearer ${MEMORY_AUTH_TOKEN}")
fi

echo "=== Cleanup (delete users from previous runs) ==="
for user in $(jq -rs 'map(.user_id) | unique[]' "$ROOT"/conversations/*.json); do
  curl -fsS "${AUTH_HEADER[@]}" -X DELETE "$BASE/users/$user" >/dev/null || true
  echo "  deleted user: $user"
done
echo ""

echo "=== Ingesting conversations ==="
for conv in "$ROOT"/conversations/*.json; do
  name=$(basename "$conv" .json)
  user_id=$(jq -r .user_id "$conv")
  count=$(jq -r '.turns | length' "$conv")
  echo "  [$name] user=$user_id turns=$count"
  jq -c '.turns[]' "$conv" | while read -r turn; do
    body=$(jq -n --argjson t "$turn" --arg u "$user_id" '{
      session_id: $t.session_id,
      user_id:    $u,
      messages:   $t.messages,
      timestamp:  $t.timestamp,
      metadata:   {}
    }')
    id=$(curl -fsS "${AUTH_HEADER[@]}" -X POST "$BASE/turns" -H 'Content-Type: application/json' -d "$body" | jq -r .id)
    echo "    turn $id"
  done
done
echo ""

echo "=== Probes ==="
pass=0
fail=0
for probe in "$ROOT"/probes/*.json; do
  name=$(jq -r .id "$probe")
  user_id=$(jq -r .user_id "$probe")
  session_id=$(jq -r .session_id "$probe")
  query=$(jq -r .query "$probe")
  max=$(jq -r .max_tokens "$probe")

  body=$(jq -n --arg q "$query" --arg s "$session_id" --arg u "$user_id" --argjson m "$max" '{
    query: $q, session_id: $s, user_id: $u, max_tokens: $m
  }')
  resp=$(curl -fsS "${AUTH_HEADER[@]}" -X POST "$BASE/recall" -H 'Content-Type: application/json' -d "$body")
  context_lower=$(jq -r .context <<<"$resp" | tr '[:upper:]' '[:lower:]')

  ok=true
  reasons=""

  while IFS= read -r s; do
    [[ -z "$s" ]] && continue
    if ! grep -qF "$s" <<<"$context_lower"; then
      ok=false
      reasons+="  missing required substring: '$s'"$'\n'
    fi
  done < <(jq -r '.expect_substrings[]?' "$probe")

  while IFS= read -r s; do
    [[ -z "$s" ]] && continue
    if grep -qF "$s" <<<"$context_lower"; then
      ok=false
      reasons+="  found forbidden substring: '$s'"$'\n'
    fi
  done < <(jq -r '.expect_absent_substrings[]?' "$probe")

  if $ok; then
    echo "  [PASS] $name"
    pass=$((pass+1))
  else
    echo "  [FAIL] $name"
    echo -n "$reasons"
    snippet=$(jq -r .context <<<"$resp" | head -c 280 | tr '\n' ' ')
    echo "    context preview: $snippet..."
    fail=$((fail+1))
  fi
done
echo ""
echo "=== Summary: $pass / $((pass+fail)) probes passed ==="
[[ $fail -eq 0 ]]
