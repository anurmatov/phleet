#!/usr/bin/env bash
# End-to-end proof of secret placeholders, with synthetic fixtures only: the real fleet-secrets
# image, a fake HTTPS upstream, and a client container using curl, git, gh, node and python exactly
# as an agent would. Exit 0: pass, 1: assertion, 2: infrastructure.
#
#   PROXY_IMAGE   fleet-secrets image to test (default: built from src/Fleet.SecretProxy/Dockerfile)
#   AGENT_IMAGE   image of the two client containers (default: the light image built from
#                 client.Dockerfile). Pass the real agent image to prove its CA trust and its gh,
#                 git, node and python against the forwarder.
#   EGRESS_SUBNET / SECRETS_SUBNET   /24 prefixes to use, as "a.b.c" (defaults 10.213.210 / 10.213.211)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
command -v docker > /dev/null || fail_infra "missing docker"

ID="fleet-secrets-e2e-$$"
TOOL_IMAGE="fleet-secrets-e2e:client"
EGRESS="${EGRESS_SUBNET:-10.213.210}"
SECRETS="${SECRETS_SUBNET:-10.213.211}"
UPSTREAM_IP="$EGRESS.10"; PROXY_IP="$SECRETS.2"; AGENT_IP="$SECRETS.20"; FOREIGN_IP="$SECRETS.21"
STATIC_SECRET="synthetic-static-secret-5d2e8a"
HOSTS=(api.example.test api.github.com github.com)
WORK="$(mktemp -d)"
chmod 755 "$WORK"
mkdir -p "$WORK/state" "$WORK/ca" "$WORK/fixture" "$WORK/log"

cleanup() {
    # KEEP=1 leaves the containers, networks and work directory in place for debugging.
    if [ -n "${KEEP:-}" ]; then echo "kept: $ID ($WORK)" >&2; return; fi
    docker rm -f "$ID-agent" "$ID-foreign" "$ID-proxy" "$ID-upstream" > /dev/null 2>&1 || true
    docker network rm "$ID-secrets" "$ID-egress" > /dev/null 2>&1 || true
    # Files the containers created are root-owned on Linux.
    docker run --rm -v "$WORK:/work" "$TOOL_IMAGE" sh -c 'rm -rf /work/*' > /dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

if [ -z "${PROXY_IMAGE:-}" ]; then
    PROXY_IMAGE="fleet-secrets-e2e:proxy"
    docker build -q -t "$PROXY_IMAGE" -f "$REPO_ROOT/src/Fleet.SecretProxy/Dockerfile" "$REPO_ROOT" > /dev/null \
        || fail_infra "could not build the fleet-secrets image"
fi
# Fixtures, the fake upstream and the log checks always run in the light image, whatever is under test.
docker build -q -t "$TOOL_IMAGE" -f "$HERE/client.Dockerfile" "$HERE" > /dev/null \
    || fail_infra "could not build the client image"
AGENT_IMAGE="${AGENT_IMAGE:-$TOOL_IMAGE}"
echo "Clients run in: $AGENT_IMAGE"
tool() { docker run --rm -i --entrypoint bash -v "$WORK:/work" -w /work "$TOOL_IMAGE" -c "$1"; }

# ── Fixtures: a stand-in public CA for the upstream, a GitHub App key, two placeholders ───────────
tool '
set -e
cd fixture
openssl req -x509 -newkey rsa:2048 -nodes -keyout public-ca.key -out public-ca.crt -subj "/CN=synthetic public CA" -days 2 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout upstream.key -out upstream.csr -subj "/CN=synthetic upstream" 2>/dev/null
printf "subjectAltName=DNS:api.example.test,DNS:api.github.com,DNS:github.com\n" > san.ext
openssl x509 -req -in upstream.csr -CA public-ca.crt -CAkey public-ca.key -CAcreateserial -out upstream.crt -days 2 -extfile san.ext 2>/dev/null
openssl genrsa -out app.pem 2048 2>/dev/null
base64 -w0 app.pem > app.pem.b64
for name in static github; do
    printf "fleet_placeholder_%s" "$(openssl rand -base64 32 | tr "+/" "-_" | tr -d "=\n")" > placeholder.$name
    printf "%s" "$(cat placeholder.$name)" | sha256sum | cut -d" " -f1 > placeholder.$name.sha256
done
chmod 644 *
' || fail_infra "could not create fixtures"
PH_STATIC="$(cat "$WORK/fixture/placeholder.static")"
PH_GITHUB="$(cat "$WORK/fixture/placeholder.github")"
[ "${#PH_STATIC}" -eq 61 ] && [ "${#PH_GITHUB}" -eq 61 ] || fail_infra "placeholder fixture has the wrong shape"
KEY_LINE="$(sed -n 2p "$WORK/fixture/app.pem")"
KEY_B64="$(head -c 48 "$WORK/fixture/app.pem.b64")"
[ "${#KEY_LINE}" -ge 40 ] && [ "${#KEY_B64}" -eq 48 ] || fail_infra "key fixture has the wrong shape"

# write_state <static value | ""> <grants: both | github | none>
write_state() {
    tool "
set -e
value='$1'; grants='$2'
static_grant='{\"agent\":\"agent1\",\"sourceIp\":\"$AGENT_IP\",\"key\":\"EXAMPLE_API_KEY\",\"placeholderSha256\":\"'\$(cat fixture/placeholder.static.sha256)'\"}'
github_grant='{\"agent\":\"agent1\",\"sourceIp\":\"$AGENT_IP\",\"key\":\"GITHUB_APP_PEM\",\"placeholderSha256\":\"'\$(cat fixture/placeholder.github.sha256)'\"}'
case \$grants in both) list=\"\$static_grant,\$github_grant\" ;; github) list=\"\$github_grant\" ;; none) list='' ;; esac
jq -n --arg value \"\$value\" --arg pem \"\$(cat fixture/app.pem.b64)\" --argjson grants \"[\$list]\" '{
  version: 1, generatedAtUtc: \"2026-01-01T00:00:00+00:00\",
  bindings: [
    {key: \"EXAMPLE_API_KEY\", kind: \"static\", hosts: [\"api.example.test\"], header: \"Authorization\", value: \$value, appId: null},
    {key: \"GITHUB_APP_PEM\", kind: \"github_app\", hosts: [\"api.github.com\", \"github.com\"], header: \"Authorization\", value: \$pem, appId: \"424242\"}
  ],
  grants: \$grants}' > state/.state.tmp
chmod 600 state/.state.tmp
mv state/.state.tmp state/state.json
" || fail_infra "could not write the state file"
}
write_state "$STATIC_SECRET" both

# ── Topology ──────────────────────────────────────────────────────────────────────────────────────
docker network create --subnet "$EGRESS.0/24" "$ID-egress" > /dev/null || fail_infra "could not create the egress network (set EGRESS_SUBNET)"
docker network create --internal --subnet "$SECRETS.0/24" "$ID-secrets" > /dev/null || fail_infra "could not create the secrets network (set SECRETS_SUBNET)"

docker run -d --name "$ID-upstream" --network "$ID-egress" --ip "$UPSTREAM_IP" --entrypoint python3 \
    -v "$HERE/upstream.py:/upstream.py:ro" -v "$WORK/fixture:/fixture:ro" -v "$WORK/log:/log" \
    "$TOOL_IMAGE" /upstream.py "$STATIC_SECRET" /log/requests.jsonl /fixture/upstream.crt /fixture/upstream.key > /dev/null

# The forwarder trusts only the stand-in CA and resolves every bound host to the fake upstream, so
# nothing in this harness can reach a real service.
add_upstream=(); for h in "${HOSTS[@]}"; do add_upstream+=(--add-host "$h:$UPSTREAM_IP"); done
docker create --name "$ID-proxy" --network "$ID-egress" "${add_upstream[@]}" \
    -e SSL_CERT_FILE=/fixture/public-ca.crt -e SSL_CERT_DIR=/nonexistent \
    -e "SecretProxy__ListenAddress=$PROXY_IP" \
    -v "$WORK/state:/var/lib/fleet-secrets/state:ro" -v "$WORK/ca:/var/lib/fleet-secrets/ca" -v "$WORK/fixture:/fixture:ro" \
    "$PROXY_IMAGE" > /dev/null
docker network connect --ip "$PROXY_IP" "$ID-secrets" "$ID-proxy"
docker start "$ID-proxy" > /dev/null
for _ in $(seq 1 30); do [ -s "$WORK/ca/ca.crt" ] && break; sleep 1; done
[ -s "$WORK/ca/ca.crt" ] || { docker logs "$ID-proxy" >&2; fail_infra "fleet-secrets did not create its CA"; }
for _ in $(seq 1 30); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' "$ID-proxy" 2>/dev/null)" = healthy ] && break
    docker exec "$ID-proxy" curl -fs http://127.0.0.1:8080/health > /dev/null 2>&1 && break
    sleep 1
done
docker exec "$ID-proxy" curl -fs http://127.0.0.1:8080/health | grep -q '"state":"loaded"' \
    || { docker logs "$ID-proxy" >&2; fail_infra "fleet-secrets is not serving a loaded state"; }
echo "PASS forwarder up: CA created, state loaded"

# What the orchestrator gives a granted agent: placeholders, steering, the CA, a fixed address.
start_client() {
    local name="$1" ip="$2" steer=()
    for h in "${HOSTS[@]}"; do steer+=(--add-host "$h:$PROXY_IP"); done
    docker create --name "$name" --network "$ID-egress" "${steer[@]}" --entrypoint sleep \
        -e "EXAMPLE_API_KEY=$PH_STATIC" -e "GH_TOKEN=$PH_GITHUB" -e "GITHUB_TOKEN=$PH_GITHUB" \
        -e NODE_EXTRA_CA_CERTS=/usr/local/share/ca-certificates/fleet-secrets.crt \
        -e REQUESTS_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt -e SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
        -e GH_PROMPT_DISABLED=1 -e GH_NO_UPDATE_NOTIFIER=1 \
        -v "$WORK/ca/ca.crt:/usr/local/share/ca-certificates/fleet-secrets.crt:ro" \
        -v "$REPO_ROOT/entrypoint.sh:/app/entrypoint.sh:ro" -v "$REPO_ROOT/gh-auth.sh:/app/gh-auth.sh:ro" \
        -v "$HERE/checks.sh:/checks.sh:ro" "$AGENT_IMAGE" infinity > /dev/null
    docker network connect --ip "$ip" "$ID-secrets" "$name"
    docker start "$name" > /dev/null
}
phase() {
    docker exec -u 0 "$1" bash /checks.sh "$2" "$STATIC_SECRET" "$KEY_LINE" "$KEY_B64" && return 0
    # The forwarder log carries no secret by design (asserted below), so it is safe to show.
    echo "--- fleet-secrets log (tail) ---" >&2
    docker logs "$ID-proxy" 2>&1 | tail -15 >&2
    fail_assert "phase $2"
}

start_client "$ID-agent" "$AGENT_IP"
phase "$ID-agent" setup
phase "$ID-agent" granted

# ── Revocation, rotation, loss of state: each takes effect on the next request ───────────────────
write_state "$STATIC_SECRET" github
phase "$ID-agent" revoked
write_state "synthetic-rotated-and-wrong" both
phase "$ID-agent" rotated
write_state "$STATIC_SECRET" both
phase "$ID-agent" restored
tool 'rm -f state/state.json'
phase "$ID-agent" stateless
write_state "$STATIC_SECRET" both
phase "$ID-agent" restored

# ── The same placeholders from another container ─────────────────────────────────────────────────
start_client "$ID-foreign" "$FOREIGN_IP"
phase "$ID-foreign" setup
phase "$ID-foreign" foreign

# ── What actually reached the upstream, and what the forwarder logged ────────────────────────────
LOG="$WORK/log/requests.jsonl"
check_log() { tool "jq -s '$2' log/requests.jsonl" | grep -qx true || fail_assert "upstream log: $1"; echo "PASS upstream log: $1"; }
check_log "no placeholder ever reached the upstream in a credential" \
    '[.[] | ((.authorization // "") + (.x_api_key // "")) | contains("fleet_placeholder_")] | any | not'
check_log "the static secret went only to its bound host" \
    "[.[] | select((.authorization // \"\") | contains(\"$STATIC_SECRET\")) | .host] | unique == [\"api.example.test\"]"
check_log "installation tokens went only to the GitHub hosts" \
    '[.[] | select((.authorization // "") | test("ghs_synthetic|^Basic ")) | .host] | unique | (. - ["api.github.com","github.com"]) == []'
check_log "the App JWT went only to the App endpoints" \
    '[.[] | select(.path | startswith("/app/")) | .host] | unique == ["api.github.com"]'
check_log "no request reached the redirect target or an unbound host" \
    '[.[] | .host] | unique | (. - ["api.example.test","api.github.com","github.com"]) == []'
check_log "the refused requests never arrived (no /v1/whoami with a query string)" \
    '[.[] | select(.path | contains("token="))] | length == 0'

docker logs "$ID-proxy" > "$WORK/proxy.log" 2>&1
for needle in "$STATIC_SECRET" "$PH_STATIC" "$PH_GITHUB" ghs_synthetic 'PRIVATE KEY' "$KEY_LINE" "$KEY_B64"; do
    grep -q -- "$needle" "$WORK/proxy.log" && fail_assert "the forwarder logged a secret, placeholder, token or key"
done
grep -q 'reason=injected' "$WORK/proxy.log" || fail_assert "the forwarder log has no injected request"
echo "PASS forwarder log: no secret, placeholder, installation token or key"
echo "PASS secret placeholders end to end"
