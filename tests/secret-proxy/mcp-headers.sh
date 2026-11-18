#!/usr/bin/env bash
# #593 A7: the pinned Claude and Codex clients in the real agent image send MCP `initialize` with two
# environment-backed headers plus mandatory identity Authorization and a separate config header,
# rendered by production code, through the unchanged fleet-secrets
# forwarder to an isolated HTTPS mock that sees both synthetic secrets and never a placeholder.
# Synthetic fixtures only; no internet route, production mount or inherited credential.
# Exit 0: pass, 1: assertion, 2: infrastructure (never a pass).
#
#   AGENT_IMAGE   required: a locally built agent image (Dockerfile at the repository root)
#   PROXY_IMAGE   fleet-secrets image (default: built from src/Fleet.SecretProxy/Dockerfile)
#   EGRESS_SUBNET / SECRETS_SUBNET   /24 prefixes as "a.b.c" (defaults 10.213.212 / 10.213.213)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
pass() { echo "PASS $*"; }
command -v docker > /dev/null || fail_infra "missing docker"
timeout 20 docker info > /dev/null 2>&1 || fail_infra "the Docker daemon is not reachable"
command -v dotnet > /dev/null || fail_infra "missing dotnet (the fixture is rendered by the production code)"
[ -n "${AGENT_IMAGE:-}" ] || fail_infra "set AGENT_IMAGE to a locally built agent image"
docker image inspect "$AGENT_IMAGE" > /dev/null 2>&1 || fail_infra "agent image $AGENT_IMAGE is not present locally"

ID="fleet-mcp-headers-$$"
TOOL_IMAGE="fleet-secrets-e2e:client"
EGRESS="${EGRESS_SUBNET:-10.213.212}"
SECRETS="${SECRETS_SUBNET:-10.213.213}"
UPSTREAM_IP="$EGRESS.10"; PROXY_IP="$SECRETS.2"; AGENT_IP="$SECRETS.20"
MCP_HOST=mcp.example.test
KEY_SECRET="synthetic-mcp-key-4a1f7c"
SECRET_SECRET="synthetic-mcp-secret-9e03b2"
CLAUDE_PIN=2.1.327
CODEX_PIN=0.170.0
WORK="$(mktemp -d)"
chmod 755 "$WORK"
mkdir -p "$WORK/state" "$WORK/ca" "$WORK/fixture" "$WORK/log" "$WORK/client"

cleanup() {
    if [ -n "${KEEP:-}" ]; then echo "kept: $ID ($WORK)" >&2; return; fi
    timeout 60 docker rm -f "$ID-agent" "$ID-proxy" "$ID-upstream" > /dev/null 2>&1 || true
    timeout 30 docker network rm "$ID-secrets" "$ID-egress" > /dev/null 2>&1 || true
    timeout 60 docker run --rm --network none -v "$WORK:/work" "$TOOL_IMAGE" sh -c 'rm -rf /work/*' > /dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

# ── The fixture, from the production planner and renderer ───────────────────────────────────────
FLEET_MCP_HEADERS_FIXTURE_DIR="$WORK/client" timeout 900 dotnet test "$REPO_ROOT/tests/Fleet.Orchestrator.Tests" \
    --filter "FullyQualifiedName~McpHeaderEnvTests.Native_fixture_export" > "$WORK/fixture-export.log" 2>&1 \
    || { tail -20 "$WORK/fixture-export.log" >&2; fail_infra "could not export the production-rendered fixture"; }
for f in claude.mcp.json codex.mcp.json codex.appsettings.json placeholders.env native-identity.json; do
    [ -s "$WORK/client/$f" ] || fail_infra "fixture file $f was not written"
done
# shellcheck disable=SC1091
. "$WORK/client/placeholders.env"
PH_KEY="$EXAMPLE_KEY_PLACEHOLDER"; PH_SECRET="$EXAMPLE_SECRET_PLACEHOLDER"
[ "${#PH_KEY}" -eq 61 ] && [ "${#PH_SECRET}" -eq 61 ] && [ "$PH_KEY" != "$PH_SECRET" ] || fail_infra "placeholder fixture has the wrong shape"
grep -q -- "$PH_KEY" "$WORK/client/claude.mcp.json" && grep -q -- "$PH_SECRET" "$WORK/client/claude.mcp.json" \
    || fail_assert "the rendered .mcp.json does not carry both placeholders"
pass "fixture rendered by the production planner and renderer"

# ── Images and certificates ─────────────────────────────────────────────────────────────────────
if [ -z "${PROXY_IMAGE:-}" ]; then
    PROXY_IMAGE="fleet-secrets-e2e:proxy"
    timeout 900 docker build -q -t "$PROXY_IMAGE" -f "$REPO_ROOT/src/Fleet.SecretProxy/Dockerfile" "$REPO_ROOT" > /dev/null \
        || fail_infra "could not build the fleet-secrets image"
fi
timeout 900 docker build -q -t "$TOOL_IMAGE" -f "$HERE/client.Dockerfile" "$HERE" > /dev/null || fail_infra "could not build the tool image"
# Fixtures, state writes and log reads run with no network at all.
tool() { timeout 120 docker run --rm -i --network none --entrypoint bash -v "$WORK:/work" -w /work "$TOOL_IMAGE" -c "$1"; }
tool '
set -e
cd fixture
openssl req -x509 -newkey rsa:2048 -nodes -keyout public-ca.key -out public-ca.crt -subj "/CN=synthetic public CA" -days 2 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout upstream.key -out upstream.csr -subj "/CN=synthetic mcp" 2>/dev/null
printf "subjectAltName=DNS:mcp.example.test\n" > san.ext
openssl x509 -req -in upstream.csr -CA public-ca.crt -CAkey public-ca.key -CAcreateserial -out upstream.crt -days 2 -extfile san.ext 2>/dev/null
chmod 644 *
' || fail_infra "could not create certificates"

# write_state <both | key-only>: the forwarder's state, as SecretProxyStateWriter writes it.
write_state() {
    local grants
    grants="{\"agent\":\"agent1\",\"sourceIp\":\"$AGENT_IP\",\"key\":\"EXAMPLE_KEY\",\"placeholderSha256\":\"$EXAMPLE_KEY_SHA256\"}"
    [ "$1" = both ] && grants="$grants,{\"agent\":\"agent1\",\"sourceIp\":\"$AGENT_IP\",\"key\":\"EXAMPLE_SECRET\",\"placeholderSha256\":\"$EXAMPLE_SECRET_SHA256\"}"
    tool "
set -e
jq -n --arg key '$KEY_SECRET' --arg secret '$SECRET_SECRET' --argjson grants '[$grants]' '{
  version: 1, generatedAtUtc: (now | todate),
  bindings: [
    {key: \"EXAMPLE_KEY\", kind: \"static\", hosts: [\"$MCP_HOST\"], header: \"X-Example-Key\", value: \$key, appId: null},
    {key: \"EXAMPLE_SECRET\", kind: \"static\", hosts: [\"$MCP_HOST\"], header: \"X-Example-Secret\", value: \$secret, appId: null}
  ],
  grants: \$grants}' > state/.state.tmp
chmod 600 state/.state.tmp
mv state/.state.tmp state/state.json
" || fail_infra "could not write the state file"
}
write_state both

# ── Topology: the agent reaches only the forwarder; only the forwarder reaches the mock ──────────
# Both networks are internal: no container in this harness has a route off the host.
timeout 30 docker network create --internal --subnet "$EGRESS.0/24" "$ID-egress" > /dev/null || fail_infra "could not create the egress network (set EGRESS_SUBNET)"
timeout 30 docker network create --internal --subnet "$SECRETS.0/24" "$ID-secrets" > /dev/null || fail_infra "could not create the secrets network (set SECRETS_SUBNET)"

timeout 60 docker run -d --name "$ID-upstream" --network "$ID-egress" --ip "$UPSTREAM_IP" --entrypoint python3 \
    -v "$HERE/mcp-upstream.py:/mcp-upstream.py:ro" -v "$WORK/fixture:/fixture:ro" -v "$WORK/log:/log" -v "$WORK/client:/client:ro" \
    "$TOOL_IMAGE" /mcp-upstream.py "$KEY_SECRET" "$SECRET_SECRET" /log/requests.jsonl /fixture/upstream.crt /fixture/upstream.key /client/native-identity.json > /dev/null \
    || fail_infra "could not start the mock MCP server"

timeout 60 docker create --name "$ID-proxy" --network "$ID-egress" --add-host "$MCP_HOST:$UPSTREAM_IP" \
    -e SSL_CERT_FILE=/fixture/public-ca.crt -e SSL_CERT_DIR=/nonexistent \
    -e "SecretProxy__ListenAddress=$PROXY_IP" -e SecretProxy__MaxStateAgeSeconds=600 \
    -v "$WORK/state:/var/lib/fleet-secrets/state:ro" -v "$WORK/ca:/var/lib/fleet-secrets/ca" -v "$WORK/fixture:/fixture:ro" \
    "$PROXY_IMAGE" > /dev/null || fail_infra "could not create the forwarder"
timeout 30 docker network connect --ip "$PROXY_IP" "$ID-secrets" "$ID-proxy"
timeout 30 docker start "$ID-proxy" > /dev/null
for _ in $(seq 1 30); do [ -s "$WORK/ca/ca.crt" ] && break; sleep 1; done
[ -s "$WORK/ca/ca.crt" ] || { docker logs "$ID-proxy" 2>&1 | tail -15 >&2; fail_infra "fleet-secrets did not create its CA"; }
for _ in $(seq 1 30); do
    timeout 10 docker exec "$ID-proxy" curl -fs http://127.0.0.1:8080/health 2> /dev/null | grep -q '"state":"loaded"' && break
    sleep 1
done
timeout 10 docker exec "$ID-proxy" curl -fs http://127.0.0.1:8080/health | grep -q '"state":"loaded"' \
    || { docker logs "$ID-proxy" 2>&1 | tail -15 >&2; fail_infra "fleet-secrets is not serving a loaded state"; }
pass "forwarder up with the unchanged image: CA created, state loaded"

# The agent as provisioning starts it: placeholders in its environment, the bound host steered to
# the forwarder, the forwarder's CA, the internal network only. Its entrypoint is not run, so no
# credential is copied in; HOME is empty.
timeout 60 docker create --name "$ID-agent" --network "$ID-secrets" --ip "$AGENT_IP" --add-host "$MCP_HOST:$PROXY_IP" \
    --entrypoint sleep -e HOME=/tmp/home -e "EXAMPLE_KEY=$PH_KEY" -e "EXAMPLE_SECRET=$PH_SECRET" \
    -e NODE_EXTRA_CA_CERTS=/usr/local/share/ca-certificates/fleet-secrets.crt \
    -e REQUESTS_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt -e SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    -v "$WORK/ca/ca.crt:/usr/local/share/ca-certificates/fleet-secrets.crt:ro" -v "$WORK/client:/client:ro" \
    "$AGENT_IMAGE" infinity > /dev/null || fail_infra "could not create the agent container"
timeout 30 docker start "$ID-agent" > /dev/null || fail_infra "could not start the agent container"
agent() { timeout "${2:-180}" docker exec -u 0 "$ID-agent" bash -c "$1"; }

# ── Isolation, before any client runs: both networks internal, no container with a default route ─
for network in "$ID-egress" "$ID-secrets"; do
    [ "$(timeout 30 docker network inspect -f '{{.Internal}}' "$network")" = true ] || fail_assert "network $network is not internal"
done
for container in "$ID-upstream" "$ID-proxy" "$ID-agent"; do
    routes="$(timeout 30 docker exec "$container" cat /proc/net/route)" || fail_infra "could not read the routes of $container"
    # Field 2 is the destination; 00000000 is a default route.
    awk 'NR > 1 && $2 == "00000000" { found = 1 } END { exit found ? 0 : 1 }' <<< "$routes" \
        && fail_assert "$container has a default route off the host"
    [ "$(timeout 30 docker inspect -f '{{len .NetworkSettings.Networks}}' "$container")" -ge 1 ] || fail_infra "$container has no network"
    for attached in $(timeout 30 docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}' "$container"); do
        [ "$attached" = "$ID-egress" ] || [ "$attached" = "$ID-secrets" ] || fail_assert "$container is attached to $attached"
    done
done
pass "isolation: both networks internal, no container has a default route or another network"
agent 'update-ca-certificates > /dev/null 2>&1 && mkdir -p /tmp/home' || fail_infra "could not trust the forwarder CA in the agent"
agent "claude --version" | grep -q "^$CLAUDE_PIN " || fail_infra "the agent image does not carry Claude CLI $CLAUDE_PIN"

LOG="$WORK/log/requests.jsonl"
received() { tool "touch log/requests.jsonl; jq -s '$1' log/requests.jsonl"; }
authenticated_inits() { received '[.[] | select(.initialize and .key_ok and .secret_ok and .identity_ok and .config_ok)] | length'; }
total_requests() { received 'length'; }
# claude mcp list: the server's line and its health check, as the CLI reports it.
claude_list() { agent 'cd /tmp/home && claude mcp list 2>&1' 240 || true; }
server_line() { grep -E "^$1:" <<< "$2" || true; }

# ── Claude: the production server entry, registered as the CLI's own user configuration ─────────
agent "cd /tmp/home && claude mcp add-json --scope user fleet-orchestrator \"\$(jq -c '.mcpServers[\"fleet-orchestrator\"]' /client/claude.mcp.json)\" > /dev/null" \
    || fail_assert "claude mcp add-json refused the production-rendered server"
before="$(authenticated_inits)"
listing="$(claude_list)"
server_line fleet-orchestrator "$listing" | grep -q "Connected" || { server_line fleet-orchestrator "$listing" >&2; fail_assert "claude does not report the example server as connected"; }
after="$(authenticated_inits)"
[ "$after" -gt "$before" ] || fail_assert "the mock saw no authenticated initialize from claude"
pass "claude $CLAUDE_PIN: initialize reached the mock with both secrets substituted, server connected"

# ── Controls: each refused by the forwarder, with no request reaching the mock ───────────────────
agent 'cd /tmp/home && claude mcp remove --scope user fleet-orchestrator > /dev/null' || fail_infra "could not remove the example server"
write_state key-only
count="$(total_requests)"
agent "cd /tmp/home && claude mcp add-json --scope user fleet-orchestrator \"\$(jq -c '.mcpServers[\"fleet-orchestrator\"]' /client/claude.mcp.json)\" > /dev/null"
server_line fleet-orchestrator "$(claude_list)" | grep -q "Connected" && fail_assert "a revoked grant still connected"
[ "$(total_requests)" -eq "$count" ] || fail_assert "a request with a revoked grant reached the mock"
pass "control: a missing grant is refused before the mock"

agent 'cd /tmp/home && claude mcp remove --scope user fleet-orchestrator > /dev/null'
write_state both
agent "cd /tmp/home && claude mcp add-json --scope user swapped \"\$(jq -c '.mcpServers[\"fleet-orchestrator\"] | .headers = {\"X-Example-Key\": .headers[\"X-Example-Secret\"], \"X-Example-Secret\": .headers[\"X-Example-Key\"]}' /client/claude.mcp.json)\" > /dev/null"
count="$(total_requests)"
server_line swapped "$(claude_list)" | grep -q "Connected" && fail_assert "swapped placeholders still connected"
[ "$(total_requests)" -eq "$count" ] || fail_assert "a placeholder in another binding's header reached the mock"
pass "control: placeholders in swapped headers are refused before the mock"

agent 'cd /tmp/home && claude mcp remove --scope user swapped > /dev/null'
agent "cd /tmp/home && claude mcp add-json --scope user fleet-orchestrator \"\$(jq -c '.mcpServers[\"fleet-orchestrator\"]' /client/claude.mcp.json)\" > /dev/null"
before="$(authenticated_inits)"
server_line fleet-orchestrator "$(claude_list)" | grep -q "Connected" || fail_assert "the original configuration no longer connects"
[ "$(authenticated_inits)" -gt "$before" ] || fail_assert "the restored configuration sent no authenticated initialize"
pass "control: the original configuration still succeeds"

# Missing and changed identity cannot initialize even with the correct config token.
for mode in missing changed; do
    agent 'cd /tmp/home && claude mcp remove --scope user fleet-orchestrator > /dev/null'
    if [[ "$mode" == missing ]]; then
        filter='del(.headers.Authorization)'
    else
        filter='.headers.Authorization = "Bearer fa1.agent.agent1.invalid"'
    fi
    agent "cd /tmp/home && claude mcp add-json --scope user fleet-orchestrator \"\$(jq -c '.mcpServers[\"fleet-orchestrator\"] | $filter' /client/claude.mcp.json)\" > /dev/null"
    before="$(authenticated_inits)"
    invalid_before="$(received '[.[] | select(.initialize and .config_ok and (.identity_ok | not))] | length')"
    server_line fleet-orchestrator "$(claude_list)" | grep -q Connected && fail_assert "invalid identity connected"
    [[ "$(authenticated_inits)" == "$before" ]] || fail_assert "invalid identity initialized"
    [[ "$(received '[.[] | select(.initialize and .config_ok and (.identity_ok | not))] | length')" -gt "$invalid_before" ]] || fail_assert "identity negative never reached the upstream"
    pass "Claude $mode identity refused with separate config token retained"
done

agent "codex --version" | grep -q "^codex-cli $CODEX_PIN$" || fail_infra "the agent image does not carry the pinned Codex CLI"

# ── Codex: the production prepare step writes config.toml, the pinned app-server connects ────────
agent '
set -e
mkdir -p /tmp/codex-root/app /tmp/codex-root/workspace
cp /client/codex.appsettings.json /tmp/codex-root/app/appsettings.json
cp /client/codex.mcp.json /tmp/codex-root/workspace/.mcp.json
cd /app && dotnet Fleet.Agent.dll prepare config --root /tmp/codex-root > /dev/null 2> /tmp/codex-prepare.err
test "$(stat -c %a /tmp/codex-root/root/.codex/config.toml)" = 600
grep -q "^http_headers = " /tmp/codex-root/root/.codex/config.toml
' || fail_assert "prepare did not write an owner-only codex config with http_headers"
pass "codex config.toml written owner-only by prepare, with http_headers"

cat > "$WORK/client-codex.py" <<'PY'
import json, os, subprocess, sys, time
env = dict(os.environ, CODEX_HOME="/tmp/codex-root/root/.codex")
proc = subprocess.Popen(["codex", "app-server", "--listen", "stdio://"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL, env=env, text=True, bufsize=1)
def send(message):
    proc.stdin.write(json.dumps(message) + "\n"); proc.stdin.flush()
def answer(rid, deadline):
    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            break
        try:
            message = json.loads(line)
        except ValueError:
            continue
        if message.get("id") == rid:
            return message
    return None
deadline = time.time() + 120
send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
      "params": {"clientInfo": {"name": "mcp-headers-acceptance", "title": "acceptance", "version": "1"}, "capabilities": {"experimentalApi": False}}})
if answer(1, deadline) is None:
    print("initialize: no answer"); proc.kill(); sys.exit(1)
send({"jsonrpc": "2.0", "method": "initialized"})
status = None
while time.time() < deadline and status is None:
    send({"jsonrpc": "2.0", "id": 2, "method": "mcpServerStatus/list", "params": {"serverName": "fleet-orchestrator"}})
    reply = answer(2, deadline)
    servers = [s for s in ((reply or {}).get("result") or {}).get("data", []) if s.get("name") == "fleet-orchestrator"]
    if servers and (servers[0].get("serverInfo") or {}).get("name") == "synthetic-mcp":
        status = servers[0]
    else:
        time.sleep(2)
proc.kill()
if status is None:
    print("fleet-orchestrator: not initialized"); sys.exit(1)
tools = status.get("tools") or {}
names = list(tools) if isinstance(tools, dict) else [t.get("name") for t in tools]
print("fleet-orchestrator: initialized, tools=" + ",".join(sorted(n for n in names if n)))
PY
before="$(authenticated_inits)"
codex_out="$(timeout 180 docker exec -u 0 -i "$ID-agent" python3 - < "$WORK/client-codex.py" 2>&1 || true)"
grep -q "^fleet-orchestrator: initialized" <<< "$codex_out" || { echo "$codex_out" >&2; fail_assert "codex did not initialize the example server"; }
[ "$(authenticated_inits)" -gt "$before" ] || fail_assert "the mock saw no authenticated initialize from codex"
pass "codex: initialize reached the mock with both secrets substituted"

# Re-render Codex config through prepare for both negative identity cases.
for mode in missing changed; do
    if [[ "$mode" == missing ]]; then filter='del(.headers.Authorization)'; else filter='.headers.Authorization = "Bearer fa1.agent.agent1.invalid"'; fi
    agent "jq '.mcpServers[\"fleet-orchestrator\"] |= ($filter)' /client/codex.mcp.json > /tmp/codex-root/workspace/.mcp.json && cd /app && dotnet Fleet.Agent.dll prepare config --root /tmp/codex-root > /dev/null 2>/dev/null" || fail_assert "negative Codex prepare failed"
    before="$(authenticated_inits)"
    invalid_before="$(received '[.[] | select(.initialize and .config_ok and (.identity_ok | not))] | length')"
    codex_out="$(timeout 180 docker exec -u 0 -i "$ID-agent" python3 - < "$WORK/client-codex.py" 2>&1 || true)"
    grep -q '^fleet-orchestrator: initialized' <<< "$codex_out" && fail_assert "invalid Codex identity connected"
    [[ "$(authenticated_inits)" == "$before" ]] || fail_assert "invalid Codex identity initialized"
    [[ "$(received '[.[] | select(.initialize and .config_ok and (.identity_ok | not))] | length')" -gt "$invalid_before" ]] || fail_assert "Codex identity negative never reached the upstream"
    pass "Codex $mode identity refused"
done

# ── What the mock saw, and what nothing kept ─────────────────────────────────────────────────────
[ "$(received '[.[] | select(.placeholder_seen)] | length')" -eq 0 ] || fail_assert "a placeholder reached the mock"
[ "$(received '[.[] | select(.host_ok | not)] | length')" -eq 0 ] || fail_assert "a request reached another host"
pass "mock: no placeholder ever arrived, only the bound host was reached"

agent "! grep -rqF -e '$KEY_SECRET' -e '$SECRET_SECRET' /tmp/home /tmp/codex-root /client && ! env | grep -qF -e '$KEY_SECRET' -e '$SECRET_SECRET'" \
    || fail_assert "a bound value is in the agent's files or environment"
timeout 30 docker logs "$ID-proxy" > "$WORK/proxy.log" 2>&1
for needle in "$KEY_SECRET" "$SECRET_SECRET" "$PH_KEY" "$PH_SECRET"; do
    grep -qF -- "$needle" "$WORK/proxy.log" && fail_assert "the forwarder logged a secret or placeholder"
done
grep -q 'reason=injected' "$WORK/proxy.log" || fail_assert "the forwarder log has no injected request"
pass "no bound value in agent files or environment, none and no placeholder in the forwarder log"
echo "PASS MCP header mappings end to end"
