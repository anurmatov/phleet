#!/usr/bin/env bash
# Wire-capture harness for #349: run the pinned claude CLI (from the built agent image)
# against a Node stub and assert the thinking/output_config fields for every effort row.
#
# Isolation: one $RUN-prefixed network, a throwaway container for the CLI, no published
# ports, cleanup removes only what this run created.
#
# Exit codes: 0 all rows pass, 1 an assertion failed, 2 infrastructure.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STUB="$REPO_ROOT/tests/claude-local-wire/stub.mjs"
RUN="clwire-$(date +%s)-$$"
NET="$RUN-net"
STUB_HOST="$RUN-stub"
CAPTURE="/tmp/$RUN-capture.jsonl"
PHASE="init"
AGENT_IMAGE="${AGENT_IMAGE:-fleet:agent}"

fail_assert() { echo "FAIL [$PHASE] $*" >&2; exit 1; }
fail_infra() { echo "INFRA [$PHASE] $*" >&2; exit 2; }
pass() { echo "PASS [$PHASE] $*"; }

cleanup() {
  local rc=$?
  docker rm -f "$RUN-cli" >/dev/null 2>&1 || true
  docker rm -f "$STUB_HOST" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  rm -f "$CAPTURE"
  exit "$rc"
}
trap cleanup EXIT

[ -x "$(command -v docker)" ] || fail_infra "docker is required"
docker image inspect "$AGENT_IMAGE" >/dev/null 2>&1 || fail_infra "agent image $AGENT_IMAGE not built"

PHASE="network"
docker network create "$NET" >/dev/null || fail_infra "network create"

PHASE="stub"
docker run -d --rm --name "$STUB_HOST" --network "$NET" \
  -v "$STUB:/stub.mjs:ro" -v "$(dirname "$CAPTURE"):/record" \
  node:22-slim node /stub.mjs "/record/$(basename "$CAPTURE")" 11434 >/dev/null \
  || fail_infra "stub container"

# One CLI turn against the stub for a given effort env/flag combination.
run_cli() {
  local label="$1" extra_env="$2" flags="$3"
  PHASE="cli:$label"
  docker run --rm --name "$RUN-cli" --network "$NET" \
    -e ANTHROPIC_BASE_URL="http://$STUB_HOST:11434" \
    -e ANTHROPIC_API_KEY= \
    -e ANTHROPIC_AUTH_TOKEN=phleet-local-no-auth \
    -e ANTHROPIC_DEFAULT_OPUS_MODEL=stub -e ANTHROPIC_DEFAULT_SONNET_MODEL=stub \
    -e ANTHROPIC_DEFAULT_HAIKU_MODEL=stub -e CLAUDE_CODE_SUBAGENT_MODEL=stub \
    $extra_env \
    "$AGENT_IMAGE" claude -p --model stub --output-format stream-json --verbose $flags \
    'say ok' >/dev/null 2>&1 || true
  docker rm -f "$RUN-cli" >/dev/null 2>&1 || true
}

assert_last_row() {
  local label="$1" want_thinking="$2" want_effort="$3"
  PHASE="assert:$label"
  local row
  row="$(tail -n 1 "$CAPTURE" 2>/dev/null || true)"
  [ -n "$row" ] || fail_assert "$label: no captured request"
  printf '%s' "$row" | grep -q "\"type\":\"$want_thinking\"" \
    || fail_assert "$label: thinking.type expected $want_thinking, got $row"
  if [ "$want_effort" = "none" ]; then
    printf '%s' "$row" | grep -q '"output_config"' \
      && fail_assert "$label: expected no output_config.effort, got $row" || true
  else
    printf '%s' "$row" | grep -q "\"effort\":\"$want_effort\"" \
      || fail_assert "$label: output_config.effort expected $want_effort, got $row"
  fi
  pass "$label"
}

# Six rows of the #349 mapping table (null / off / low / medium / xhigh + the CLI default).
run_cli default '' ''
assert_last_row default-no-flag adaptive high

run_cli xhigh '' '--effort xhigh'
assert_last_row xhigh adaptive xhigh

run_cli low '' '--effort low'
assert_last_row low adaptive low

run_cli medium '' '--effort medium'
assert_last_row medium adaptive medium

run_cli off '-e CLAUDE_CODE_EXTRA_BODY={"thinking":{"type":"disabled"}}' ''
assert_last_row off disabled none

# The env override case: CLAUDE_CODE_EFFORT_LEVEL must win over --effort when inherited —
# this is why local mode strips it (#349 MUST NOT 4).
run_cli env-override '-e CLAUDE_CODE_EFFORT_LEVEL=low' '--effort xhigh'
assert_last_row env-override adaptive low

PHASE="done"
pass "all wire rows"
