#!/usr/bin/env bash
# Real scripts and Compose parsing; Docker lifecycle/builds are simulated.
# Not host upgrade acceptance.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/repo/scripts/lib" "$DIR/repo/fleet" "$DIR/bin"
cp "$ROOT/setup.sh" "$ROOT/upgrade.sh" "$ROOT/docker-compose.example.yml" "$DIR/repo/"
cp "$ROOT/scripts/lib/comms-profiles.sh" "$DIR/repo/scripts/lib/"
REAL_DOCKER=$(command -v docker)
export REAL_DOCKER CALLS="$DIR/calls" CONFIG="$DIR/config.json"
cat > "$DIR/bin/docker" <<'WRAPPER'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CALLS"
case "$1" in
  info|build) exit 0 ;;
  compose)
    # Validate the exact project's service env_file with real Compose before
    # simulating any lifecycle call; a missing repo-root .env must fail here.
    args=(compose)
    shift
    while [[ $# -gt 0 ]]; do
      case "$1" in
        -p|-f|--env-file|--profile) args+=("$1" "$2"); shift 2 ;;
        *) break ;;
      esac
    done
    "$REAL_DOCKER" "${args[@]}" config --format json > "$CONFIG"
    case "$1" in
      ps) echo synthetic-running ;;
      pull)
        python3 - "$CONFIG" <<'PYCODE'
import json, sys
with open(sys.argv[1]) as stream:
    config = json.load(stream)
assert config["services"]["comms-seaweedfs"]["image"] == "chrislusf/seaweedfs:4.48@sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d"
PYCODE
        [[ "${FAIL_PULL:-false}" != true ]]
        ;;
      rm)
        python3 - "$CONFIG" "${@:3}" <<'PYCODE'
import json, sys
with open(sys.argv[1]) as stream:
    services = json.load(stream)["services"]
assert all(service in services for service in sys.argv[2:]), sys.argv[2:]
PYCODE
        ;;
      down) ;;
      *) exit 99 ;;
    esac
    ;;
  *) exit 99 ;;
esac
WRAPPER
chmod +x "$DIR/bin/docker"
export PATH="$DIR/bin:$PATH"

for mode in disabled seaweedfs; do
  cp "$ROOT/.env.example" "$DIR/repo/fleet/.env"
  cat >> "$DIR/repo/fleet/.env" <<ENV
FLEET_BASE_DIR=$DIR/repo/fleet
FLEET_COMMS_ENABLED=$([[ "$mode" == disabled ]] && echo false || echo true)
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-seaweedfs:8333
FLEET_COMMS_MEDIA_STORE=seaweedfs
ENV
  cp "$DIR/repo/fleet/.env" "$DIR/before"
  # Old generated definitions must not determine the new pin/services.
  printf 'services:\n  old-service:\n    image: busybox\n' > "$DIR/repo/fleet/docker-compose.yml"
  : > "$CALLS"
  bash "$DIR/repo/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; exit 1; }
  [[ ! -e "$DIR/repo/.env" ]]
  cmp "$DIR/before" "$DIR/repo/fleet/.env"
  grep -q 'Fleet upgrade complete' "$DIR/output"
  grep -q ' down$' "$CALLS"
  if [[ "$mode" == disabled ]]; then
    grep -q ' rm -sf fleet-comms ' "$CALLS"
    ! grep -q ' pull ' "$CALLS"
  else
    grep -q ' pull comms-seaweedfs$' "$CALLS"
    ! grep -q ' rm -sf ' "$CALLS"
  fi
  [[ -z "$(find "$DIR/repo/fleet" -name '.compose-next.*' -print)" ]]
  echo "PASS actual upgrade script and real Compose config without repository .env ($mode); lifecycle simulated"
done

# A failed preflight pull must keep the old generated definition and runtime.
printf 'services:\n  old-service:\n    image: busybox\n' > "$DIR/repo/fleet/docker-compose.yml"
cp "$DIR/repo/fleet/docker-compose.yml" "$DIR/old-compose"
: > "$CALLS"
rc=0
FAIL_PULL=true bash "$DIR/repo/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || rc=$?
[[ "$rc" == 1 ]]
cmp "$DIR/old-compose" "$DIR/repo/fleet/docker-compose.yml"
! grep -Eq ' down$| rm |^build ' "$CALLS"
[[ -z "$(find "$DIR/repo/fleet" -name '.compose-next.*' -print)" ]]
echo 'PASS failed preflight pull preserves old Compose before lifecycle; preview removed'

# Execute setup's actual pull statement against its generated runtime Compose.
sed -E -e 's|^(      context: )\.$|\1..|' -e 's|\./deploy/comms-seaweedfs:|../deploy/comms-seaweedfs:|' "$ROOT/docker-compose.example.yml" > "$DIR/repo/fleet/docker-compose.yml"
line=$(sed -n '/docker compose .* pull comms-seaweedfs/ { s/ *\\$//; p; }' "$ROOT/setup.sh")
[[ -n "$line" ]]
SCRIPT_DIR="$DIR/repo" FLEET_BASE_DIR="$DIR/repo/fleet" \
  COMPOSE_PROJECT=fleet COMPOSE_EXAMPLE="$DIR/repo/docker-compose.example.yml" \
  COMPOSE_FILE="$DIR/repo/fleet/docker-compose.yml" ENV_FILE="$DIR/repo/fleet/.env" \
  bash -eu -c "$line"
[[ ! -e "$DIR/repo/.env" ]]
echo 'PASS actual setup pull statement resolves runtime service env_file; pull simulated'
