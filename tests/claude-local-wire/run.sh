#!/usr/bin/env bash
# Wire-capture harness for #349: run the pinned Claude Code CLI against a stub Anthropic endpoint
# and assert what it sends in `thinking` and `output_config.effort` for every local-mode effort row.
#
# Two ways to supply the CLI, both checked against the Dockerfile pin before any row runs:
#   docker (default)  AGENT_IMAGE=fleet:agent — the CLI inside the built agent image. One
#                     $RUN-prefixed network, throwaway containers, no published ports; cleanup
#                     removes only what this run created.
#   host              CLAUDE_BIN=/path/to/claude — a host binary of the pinned version, run with an
#                     empty environment (`env -i`) and a throwaway HOME, so no host credential,
#                     config or inherited thinking variable can reach it. Needs node for the stub.
#
# The ROWS table is the contract: `ClaudeLocalModelWireHarnessTests` checks that its flag and
# extra-body columns are exactly what ClaudeLocalModel produces for each Effort value.
#
# Exit codes: 0 all rows pass, 1 an assertion failed, 2 infrastructure.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STUB="$REPO_ROOT/tests/claude-local-wire/stub.mjs"
PIN="$(sed -n 's/^ARG CLAUDE_CODE_VERSION=//p' "$REPO_ROOT/Dockerfile" | head -1)"
RUN="clwire-$(date +%s)-$$"
WORK="$(mktemp -d "/tmp/$RUN.XXXX")"
CAPTURE="$WORK/capture.txt"
PHASE="init"
MODE="docker"
[ -n "${CLAUDE_BIN:-}" ] && MODE="host"
AGENT_IMAGE="${AGENT_IMAGE:-fleet:agent}"
STUB_PID=""

fail_assert() { echo "FAIL [$PHASE] $*" >&2; exit 1; }
fail_infra() { echo "INFRA [$PHASE] $*" >&2; exit 2; }
pass() { echo "PASS [$PHASE] $*"; }

cleanup() {
  local rc=$?
  if [ "$MODE" = "docker" ]; then
    docker rm -f "$RUN-cli" "$RUN-stub" >/dev/null 2>&1 || true
    docker network rm "$RUN-net" >/dev/null 2>&1 || true
  elif [ -n "$STUB_PID" ]; then
    kill "$STUB_PID" >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK"
  exit "$rc"
}
trap cleanup EXIT

# The claude child's local-mode environment (ClaudeLocalModel.BuildEnvironment), with the model
# tag and base URL of the stub. The effort-specific variables are added per row below.
LOCAL_ENV=(
  ANTHROPIC_API_KEY=
  ANTHROPIC_AUTH_TOKEN=phleet-local-no-auth
  CLAUDE_CODE_ATTRIBUTION_HEADER=0
  CLAUDE_CODE_TOTAL_TOKENS_REMINDER=off
  DISABLE_ERROR_REPORTING=1
  DISABLE_FEEDBACK_COMMAND=1
  CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY=1
  CLAUDE_CODE_AUTO_MODE_SERVER=0
  ANTHROPIC_DEFAULT_OPUS_MODEL=stub
  ANTHROPIC_DEFAULT_SONNET_MODEL=stub
  ANTHROPIC_DEFAULT_HAIKU_MODEL=stub
  CLAUDE_CODE_SUBAGENT_MODEL=stub
)

# rows:begin
# label|Effort|--effort value|CLAUDE_CODE_EXTRA_BODY|want thinking.type|want output_config.effort
# `today-null` is the pre-#349 local agent (no flag): the regression the null row replaces.
ROWS=(
  'today-null|-|none|none|adaptive|high'
  'null|null|xhigh|none|adaptive|xhigh'
  'off|off|none|{"thinking":{"type":"disabled"}}|disabled|high'
  'low|low|low|none|adaptive|low'
  'medium|medium|medium|none|adaptive|medium'
  'xhigh|xhigh|xhigh|none|adaptive|xhigh'
)
# rows:end

# ── infrastructure ─────────────────────────────────────────────────────────────
[ -n "$PIN" ] || fail_infra "could not read ARG CLAUDE_CODE_VERSION from the Dockerfile"

if [ "$MODE" = "docker" ]; then
  PHASE="docker"
  command -v docker >/dev/null || fail_infra "docker is required (or set CLAUDE_BIN for host mode)"
  docker image inspect "$AGENT_IMAGE" >/dev/null 2>&1 || fail_infra "agent image $AGENT_IMAGE not built"
  VERSION="$(docker run --rm --entrypoint claude "$AGENT_IMAGE" --version 2>/dev/null || true)"
  docker network create "$RUN-net" >/dev/null || fail_infra "network create"
  docker run -d --name "$RUN-stub" --network "$RUN-net" \
    -v "$STUB:/stub.mjs:ro" -v "$WORK:/record" \
    node:22-slim node /stub.mjs /record/capture.txt 11434 >/dev/null || fail_infra "stub container"
  BASE_URL="http://$RUN-stub:11434"
else
  PHASE="host"
  command -v node >/dev/null || fail_infra "node is required for the stub in host mode"
  VERSION="$("$CLAUDE_BIN" --version 2>/dev/null || true)"
  PORT=$((20000 + RANDOM % 20000))
  node "$STUB" "$CAPTURE" "$PORT" 127.0.0.1 >"$WORK/stub.log" 2>&1 &
  STUB_PID=$!
  BASE_URL="http://127.0.0.1:$PORT"
fi

PHASE="pin"
case "$VERSION" in
  "$PIN "*) pass "claude $VERSION ($MODE mode) matches the Dockerfile pin $PIN" ;;
  *) fail_infra "claude reports '$VERSION'; the Dockerfile pins $PIN — the wire shapes are version-specific" ;;
esac

PHASE="stub"
for _ in $(seq 1 50); do
  if [ "$MODE" = "host" ]; then grep -q listening "$WORK/stub.log" 2>/dev/null && break
  else docker logs "$RUN-stub" 2>&1 | grep -q listening && break; fi
  sleep 0.2
done
touch "$CAPTURE"

# One CLI turn against the stub. $1 flag value or "none"; remaining args are extra VAR=value pairs.
run_cli() {
  local flag="$1"; shift
  local effort_args=()
  [ "$flag" != "none" ] && effort_args=(--effort "$flag")
  if [ "$MODE" = "host" ]; then
    local home="$WORK/home-$RANDOM"; mkdir -p "$home"
    env -i PATH="$PATH" HOME="$home" ANTHROPIC_BASE_URL="$BASE_URL" "${LOCAL_ENV[@]}" "$@" \
      timeout 120 "$CLAUDE_BIN" -p --model stub --output-format stream-json --verbose \
      "${effort_args[@]}" 'say ok' </dev/null >/dev/null 2>&1 || true
  else
    local envs=() pair
    for pair in "ANTHROPIC_BASE_URL=$BASE_URL" "${LOCAL_ENV[@]}" "$@"; do envs+=(-e "$pair"); done
    docker run --rm --name "$RUN-cli" --network "$RUN-net" "${envs[@]}" --entrypoint claude "$AGENT_IMAGE" \
      -p --model stub --output-format stream-json --verbose "${effort_args[@]}" 'say ok' \
      </dev/null >/dev/null 2>&1 || true
  fi
}

# Runs one row and asserts the turn's messages request. $1 label, $2 flag, $3 want thinking,
# $4 want effort, remaining args extra VAR=value pairs.
check_row() {
  local label="$1" flag="$2" want_thinking="$3" want_effort="$4"; shift 4
  PHASE="row:$label"
  local before after row
  before="$(wc -l <"$CAPTURE")"
  run_cli "$flag" "$@"
  after="$(wc -l <"$CAPTURE")"
  [ "$after" -gt "$before" ] || fail_assert "no /v1/messages request captured"
  row="$(tail -n 1 "$CAPTURE")"
  [ "$row" = "thinking=$want_thinking effort=$want_effort" ] \
    || fail_assert "want 'thinking=$want_thinking effort=$want_effort', got '$row'"
  pass "$row"
}

for spec in "${ROWS[@]}"; do
  IFS='|' read -r label _effort flag extra want_thinking want_effort <<<"$spec"
  if [ "$extra" = "none" ]; then
    check_row "$label" "$flag" "$want_thinking" "$want_effort"
  else
    check_row "$label" "$flag" "$want_thinking" "$want_effort" "CLAUDE_CODE_EXTRA_BODY=$extra"
  fi
done

# An inherited CLAUDE_CODE_EFFORT_LEVEL beats --effort, which is why local mode strips it.
check_row env-override xhigh adaptive low CLAUDE_CODE_EFFORT_LEVEL=low

PHASE="done"
pass "all $(( ${#ROWS[@]} + 1 )) wire rows"
