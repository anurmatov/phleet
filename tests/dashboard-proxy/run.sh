#!/usr/bin/env bash
# Isolated acceptance harness for #341 — the dashboard nginx proxy must
# re-resolve fleet-orchestrator per request instead of caching one container
# address for the process lifetime.
#
# Everything is $RUN-prefixed on its own network: no Compose, no fixed
# fleet-* names, no published ports, so it can run beside a live stack.
# Cleanup removes only the exact names this run created.
#
# Exit codes: 0 all phases pass, 1 an assertion failed, 2 infrastructure.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
NGINX_CONF="${NGINX_CONF:-$REPO_ROOT/src/fleet-dashboard/nginx.conf}"
DASHBOARD_IMAGE="${DASHBOARD_IMAGE:-}"
STUB_SCRIPT="$REPO_ROOT/tests/dashboard-proxy/stub_orchestrator.py"
RUN="dashproxy-$(date +%s)-$$"
RUN_ID="$RUN"
PHASE="init"
STATIC_DIR=""
BODY_FILE="/tmp/dashproxy-body"
WS_KEY="dGhlIHNhbXBsZSBub25jZQ=="
DASH_HOST=""

fail_assert() { echo "FAIL [$PHASE] $*" >&2; exit 1; }
fail_infra() { echo "INFRA [$PHASE] $*" >&2; exit 2; }
pass() { echo "PASS [$PHASE] $*"; }

contains() { printf '%s' "$1" | grep -qF -- "$2"; }

cleanup() {
  local rc=$?
  local name
  for name in "$RUN-dash" "$RUN-dash2" "$RUN-client" "$RUN-orch-a" "$RUN-orch-b" "$RUN-orch-c"; do
    docker rm -f "$name" >/dev/null 2>&1 || echo "WARN: cleanup could not remove $name"
  done
  # docker rm -f can return before the last endpoint detaches; give the
  # network a bounded moment to become removable instead of leaking it.
  if ! docker network rm "$RUN-net" >/dev/null 2>&1; then
    local removed=0 attempt
    for attempt in 1 2 3 4 5; do
      sleep 2
      if docker network rm "$RUN-net" >/dev/null 2>&1; then removed=1; break; fi
    done
    [ "$removed" = "1" ] || echo "WARN: cleanup could not remove network $RUN-net"
  fi
  if [ -n "$STATIC_DIR" ]; then rm -rf "$STATIC_DIR"; fi
  exit "$rc"
}
trap cleanup EXIT

# Runs a docker command; any failure is infrastructure (daemon, pull, create),
# never a proxy verdict.
infra() {
  local label="$1"
  shift
  if ! "$@" >/dev/null 2>&1; then
    fail_infra "$label"
  fi
}

resolve_conf() {
  local p="$1"
  if [ -f "$p" ]; then
    (cd "$(dirname "$p")" && printf '%s/%s' "$(pwd)" "$(basename "$p")")
  elif [ -f "$REPO_ROOT/$p" ]; then
    printf '%s' "$REPO_ROOT/$p"
  else
    echo "INFRA [$PHASE] nginx conf not found: $p" >&2
    exit 2
  fi
}

start_stub() { # start_stub <A|B|C> — $RUN-orch-<x> with the shared alias
  local letter="$1"
  infra "start stub orchestrator $letter" docker run -d \
    --name "$RUN-orch-$letter" \
    --network "$RUN-net" \
    --network-alias fleet-orchestrator \
    -e "STUB_NAME=$letter" \
    -v "$STUB_SCRIPT":/stub/stub_orchestrator.py:ro \
    python:3.12-alpine \
    python /stub/stub_orchestrator.py
}

start_dash() { # start_dash <container-name> — mounted conf, or DASHBOARD_IMAGE unmodified
  local name="$1"
  if [ -n "$DASHBOARD_IMAGE" ]; then
    infra "start dashboard $name from image $DASHBOARD_IMAGE" docker run -d \
      --name "$name" \
      --network "$RUN-net" \
      "$DASHBOARD_IMAGE"
  else
    infra "start dashboard $name with mounted conf" docker run -d \
      --name "$name" \
      --network "$RUN-net" \
      -v "$NGINX_CONF":/etc/nginx/conf.d/default.conf:ro \
      -v "$STATIC_DIR":/usr/share/nginx/html:ro \
      nginx:alpine
  fi
}

# One request through the client container. Sets PROBE_STATUS, PROBE_TIME, PROBE_BODY.
probe() { # probe <path> [max-time]
  local path="$1" maxtime="${2:-3}" out
  out=$(docker exec "$RUN-client" curl -s --max-time "$maxtime" -o "$BODY_FILE" \
    -w '%{http_code} %{time_total}' "http://$DASH_HOST$path") || out="000 0.000"
  PROBE_STATUS="${out%% *}"
  PROBE_TIME="${out##* }"
  PROBE_BODY=$(docker exec "$RUN-client" cat "$BODY_FILE" 2>/dev/null || true)
}

# Bounded polling (never a single timing-sensitive probe): up to $3 attempts,
# 1 s apart, for status 200 whose body contains $2. Echoes "<attempts> <seconds>".
await_http() { # await_http <url> <needle> [attempts=30] [max-time=3]
  local url="$1" needle="$2" attempts="${3:-30}" maxtime="${4:-3}"
  local start="$SECONDS" i=1 status="000" body=""
  while [ "$i" -le "$attempts" ]; do
    local out
    out=$(docker exec "$RUN-client" curl -s --max-time "$maxtime" -o "$BODY_FILE" \
      -w '%{http_code}' "$url") || out="000"
    status="$out"
    body=$(docker exec "$RUN-client" cat "$BODY_FILE" 2>/dev/null || true)
    if [ "$status" = "200" ] && contains "$body" "$needle"; then
      echo "$i $((SECONDS - start))"
      return 0
    fi
    i=$((i + 1))
    sleep 1
  done
  echo "await $url: exhausted after $attempts attempts; last status=$status body=${body:0:200}" >&2
  return 1
}

# WebSocket upgrade probe: sends a real handshake, reads the response head.
# Sets WS_STATUS (first response line) and WS_OUTPUT (full head).
ws_probe() { # ws_probe <path>
  local path="$1" rc=0
  # A stub-side close after the 101 head can make curl exit non-zero, so keep
  # whatever head was received — the status line is the evidence, not rc.
  WS_OUTPUT=$(docker exec "$RUN-client" curl -si -N --max-time 3 \
    -H "Connection: Upgrade" \
    -H "Upgrade: websocket" \
    -H "Sec-WebSocket-Version: 13" \
    -H "Sec-WebSocket-Key: $WS_KEY" \
    "http://$DASH_HOST$path") || rc=$?
  WS_STATUS=$(printf '%s\n' "$WS_OUTPUT" | head -1)
}

container_ip() { # container_ip <name>
  docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$1" 2>/dev/null || true
}

# ── pre-flight ────────────────────────────────────────────────────────────────

PHASE="pre-flight"
NGINX_CONF="$(resolve_conf "$NGINX_CONF")"
[ -f "$STUB_SCRIPT" ] || fail_infra "missing $STUB_SCRIPT"
docker info >/dev/null 2>&1 || fail_infra "docker daemon unreachable"
STATIC_DIR="$(mktemp -d)"
printf '<!doctype html><title>dashproxy</title>ok\n' > "$STATIC_DIR/index.html"
# mktemp -d is 0700/root; the nginx worker (uid 101) must traverse and read it.
chmod 0755 "$STATIC_DIR"
chmod 0644 "$STATIC_DIR/index.html"

infra "create network $RUN-net" docker network create "$RUN-net"

# ── P1 baseline ───────────────────────────────────────────────────────────────

PHASE="P1 baseline"
start_stub A
infra "start verifier client" docker run -d \
  --name "$RUN-client" \
  --network "$RUN-net" \
  --entrypoint sleep \
  curlimages/curl:8.10.1 900

# Resolve the alias directly from the client BEFORE starting the dashboard, so
# that with main's startup-resolving conf the negative control fails at P2 (the
# recreate), not at P1 on a slow DNS registration.
if ! rec=$(await_http "http://fleet-orchestrator:3600/?direct=1" '"instance":"A"'); then
  fail_infra "stub A never answered on the alias"
fi

DASH_HOST="$RUN-dash"
start_dash "$RUN-dash"
if ! rec=$(await_http "http://$DASH_HOST/api/setup/status" '"instance":"A"'); then
  fail_assert "baseline: no proxied request reached instance A (last: $rec)"
fi
pass "baseline instance A after ${rec%% *} attempts, ${rec##* }s"

probe "/"
[ "$PROBE_STATUS" = "200" ] || fail_assert "static / expected 200, got $PROBE_STATUS"
pass "static / -> 200"

# ── P1 preservation ───────────────────────────────────────────────────────────

PHASE="P1 preservation"
probe "/api/agents?limit=2"
[ "$PROBE_STATUS" = "200" ] || fail_assert "/api/agents?limit=2 expected 200, got $PROBE_STATUS"
contains "$PROBE_BODY" '"path":"/api/agents?limit=2"' || fail_assert "request URI rewritten: $PROBE_BODY"
contains "$PROBE_BODY" "\"host\":\"$RUN-dash\"" || fail_assert "Host header not preserved: $PROBE_BODY"
if contains "$PROBE_BODY" '"xRealIp":null'; then fail_assert "X-Real-IP dropped: $PROBE_BODY"; fi
contains "$PROBE_BODY" '"xRealIp":"' || fail_assert "X-Real-IP missing: $PROBE_BODY"
pass "/api/ preserves URI, Host and X-Real-IP"

probe "/health"
[ "$PROBE_STATUS" = "200" ] || fail_assert "/health expected 200, got $PROBE_STATUS"
contains "$PROBE_BODY" '"host":"fleet-orchestrator:3600"' || fail_assert "upstream Host changed: $PROBE_BODY"
pass "/health preserves upstream Host"

ws_probe "/ws"
contains "$WS_STATUS" " 101 " || fail_assert "/ws expected 101, got '$WS_STATUS'"
contains "$WS_OUTPUT" "X-Stub: A" || fail_assert "/ws reached the wrong upstream: $WS_OUTPUT"
ws_probe "/ws/logs/agent1"
contains "$WS_STATUS" " 101 " || fail_assert "/ws/logs/agent1 expected 101, got '$WS_STATUS'"
contains "$WS_OUTPUT" "X-Stub-Path: /ws/logs/agent1" || fail_assert "WS path rewritten: $WS_OUTPUT"
pass "/ws and /ws/logs/* upgrade with unchanged URI"

# ── P2 recreate ───────────────────────────────────────────────────────────────

PHASE="P2 recreate"
IP_A="$(container_ip "$RUN-orch-a")"
STATE_BEFORE="$(docker inspect -f '{{.State.StartedAt}} {{.RestartCount}}' "$RUN-dash")" \
  || fail_infra "inspect $RUN-dash"
start_stub B
infra "remove stub A" docker rm -f "$RUN-orch-a"
IP_B="$(container_ip "$RUN-orch-b")"
{ [ -n "$IP_A" ] && [ -n "$IP_B" ] && [ "$IP_A" != "$IP_B" ]; } \
  || fail_assert "orchestrator address did not change ($IP_A -> $IP_B)"

if ! rec=$(await_http "http://$DASH_HOST/api/setup/status" '"instance":"B"'); then
  fail_assert "recreate recovery: no attempt reached instance B (last: $rec)"
fi
pass "instance B after ${rec%% *} attempts, ${rec##* }s (address $IP_A -> $IP_B)"

ws_probe "/ws"
contains "$WS_STATUS" " 101 " || fail_assert "/ws expected 101 after recreate, got '$WS_STATUS'"
contains "$WS_OUTPUT" "X-Stub: B" || fail_assert "/ws still targets the old instance: $WS_OUTPUT"
pass "/ws targets instance B after recreate"

STATE_AFTER="$(docker inspect -f '{{.State.StartedAt}} {{.RestartCount}}' "$RUN-dash")" \
  || fail_infra "inspect $RUN-dash"
[ "$STATE_BEFORE" = "$STATE_AFTER" ] \
  || fail_assert "dashboard restarted during recovery ($STATE_BEFORE -> $STATE_AFTER)"
pass "dashboard was not restarted or reloaded"

# ── P2 no stale target ────────────────────────────────────────────────────────

PHASE="P2 no stale target"
# Wait out nginx's resolver cache (valid=10s) before checking for the old IP,
# so a correct fix cannot fail on its own cache window.
sleep 12
LOG_LINES_BEFORE="$(docker logs "$RUN-dash" 2>&1 | wc -l)" || fail_infra "docker logs $RUN-dash"
for i in $(seq 1 10); do
  probe "/api/setup/status"
  if [ "$PROBE_STATUS" != "200" ] || ! contains "$PROBE_BODY" '"instance":"B"'; then
    fail_assert "request $i/10 after cache expiry: status=$PROBE_STATUS body=$PROBE_BODY"
  fi
done
NEW_LOGS="$(docker logs "$RUN-dash" 2>&1 | tail -n +"$((LOG_LINES_BEFORE + 1))")" \
  || fail_infra "docker logs $RUN-dash"
if contains "$NEW_LOGS" "$IP_A"; then
  fail_assert "dashboard log mentions the old address $IP_A: $NEW_LOGS"
fi
pass "10/10 requests hit instance B; no log line targets $IP_A"

# ── P3 orchestrator absent ────────────────────────────────────────────────────

PHASE="P3 orchestrator absent"
infra "remove stub B" docker rm -f "$RUN-orch-b"
sleep 12

RECOVERED=""
for i in $(seq 1 10); do
  probe "/" 
  [ "$PROBE_STATUS" = "200" ] || fail_assert "static / broke while orchestrator absent ($PROBE_STATUS)"
  probe "/api/setup/status" 8
  if [ "$PROBE_STATUS" = "502" ] && awk -v t="$PROBE_TIME" 'BEGIN { exit !(t < 7) }'; then
    RECOVERED="$PROBE_TIME"
    break
  fi
  sleep 1
done
[ -n "$RECOVERED" ] || fail_assert "expected a fast 502 (<7s) while the orchestrator is absent"
pass "orchestrator absent -> 502 in ${RECOVERED}s; static / stayed 200"

# ── P3 return ─────────────────────────────────────────────────────────────────

PHASE="P3 return"
start_stub C
if ! rec=$(await_http "http://$DASH_HOST/api/setup/status" '"instance":"C"'); then
  fail_assert "return: no proxied request reached instance C (last: $rec)"
fi
pass "instance C after ${rec%% *} attempts, ${rec##* }s"

# ── P4 cold start ─────────────────────────────────────────────────────────────

PHASE="P4 cold start"
infra "remove stub C" docker rm -f "$RUN-orch-c"
start_dash "$RUN-dash2"
DASH_HOST="$RUN-dash2"
sleep 3
RUNNING="$(docker inspect -f '{{.State.Running}}' "$RUN-dash2")" || fail_infra "inspect $RUN-dash2"
[ "$RUNNING" = "true" ] || fail_assert "dashboard did not stay up without an orchestrator"
probe "/"
[ "$PROBE_STATUS" = "200" ] || fail_assert "static / expected 200 on cold start, got $PROBE_STATUS"
probe "/api/setup/status"
[ "$PROBE_STATUS" = "502" ] || fail_assert "proxied route expected 502 on cold start, got $PROBE_STATUS"
pass "dashboard starts with no orchestrator; static 200, proxied 502"

echo "PASS all phases ($RUN_ID)"
