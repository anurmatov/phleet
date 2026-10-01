#!/usr/bin/env bash
# Real Compose parsing with lifecycle commands refused. Not M1-M9 runtime evidence.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/bin" "$DIR/repo/scripts" "$DIR/repo/deploy"
cp "$ROOT/scripts/smoke-comms-media-store.sh" "$DIR/repo/scripts/"
cp "$ROOT/docker-compose.example.yml" "$DIR/repo/"
export REAL_DOCKER=$(command -v docker)
export CALLS="$DIR/calls"
export CONFIG="$DIR/config.json"
export GUARDS="$DIR/network-guards"
cat > "$DIR/bin/docker" <<'MOCK'
#!/usr/bin/env bash
case "$*" in
  'network inspect '*)
    printf '%s\n' "$3" >> "$GUARDS"
    [[ "$3" == "${COLLIDE_NETWORK:-}" ]] ;;
  'container inspect '*|'volume inspect '*) exit 1 ;;
  'compose '*' config '*)
    "$REAL_DOCKER" "$@" > "$CONFIG" || exit $?
    cat "$CONFIG" ;;
  'inspect -f '*)
    python3 - "$CONFIG" "$3" <<'PYCODE'
import json, sys
network=json.load(open(sys.argv[1]))["networks"]["comms-media"]["name"]
assert f'"{network}"' in sys.argv[2], sys.argv[2]
print("192.0.2.9")
PYCODE
    ;;
  'pull '*) printf '%s\n' "$2" > "$CALLS"; exit 72 ;;
  'compose '*' down'|'rm -f '*|'volume rm '*) exit 0 ;;
  *) echo "unexpected lifecycle command: $*" >&2; exit 99 ;;
esac
MOCK
chmod +x "$DIR/bin/docker"
export PATH="$DIR/bin:$PATH"
export SEAWEEDFS_IMAGE=chrislusf/seaweedfs:4.48@sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d
export GRPCURL_IMAGE=unused-fixture
rc=0
bash "$DIR/repo/scripts/smoke-comms-media-store.sh" > "$DIR/output" 2>&1 || rc=$?
if [[ "$rc" != 72 ]]; then cat "$DIR/output" >&2; exit 1; fi
[[ "$(cat "$CALLS")" == "$SEAWEEDFS_IMAGE" ]]
[[ ! -e "$DIR/repo/.env" ]]
echo 'PASS smoke resolves isolated service env_file before attempting the approved pin'

# Real Compose must resolve a fixture-only name despite the example's fixed name.
NETWORK=$(python3 - "$CONFIG" <<'PYCODE'
import json, sys
config=json.load(open(sys.argv[1]))
network=config["networks"]["comms-media"]
assert network["name"] == "comms397-smoke_comms-media", network
assert network["internal"] is True
assert list(config["services"]["comms-seaweedfs"]["networks"]) == ["comms-media"]
print(network["name"])
PYCODE
)
[[ "$(cat "$GUARDS")" == "$NETWORK" ]]
# Exercise the actual lookup statement with the rendered network and a fake inspect response.
lookup=$(sed -n '/^IPV4=/p' "$ROOT/scripts/smoke-comms-media-store.sh")
[[ -n "$lookup" ]]
NETWORK="$NETWORK" bash -c "$lookup; [[ \"\$IPV4\" == 192.0.2.9 ]]"
if NETWORK=wrong-fixture-network bash -c "$lookup; [[ \"\$IPV4\" == 192.0.2.9 ]]" > "$DIR/wrong-lookup" 2>&1; then exit 1; fi
echo 'PASS actual Compose name matches collision guard and IPv4 lookup; wrong lookup rejected'
# Refuse a pre-existing fixture network before Compose, pulls or lifecycle changes.
rm -f "$CONFIG" "$CALLS" "$GUARDS"
rc=0
COLLIDE_NETWORK="$NETWORK" bash "$DIR/repo/scripts/smoke-comms-media-store.sh" > "$DIR/collision" 2>&1 || rc=$?
[[ "$rc" == 1 && ! -e "$CONFIG" && ! -e "$CALLS" ]]
grep -q "Refusing to touch existing network $NETWORK" "$DIR/collision"
echo 'PASS fixture network collision refuses before any Compose or image operation'
