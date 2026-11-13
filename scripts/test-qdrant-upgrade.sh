#!/usr/bin/env bash
# The Qdrant release check (scripts/lib/qdrant-upgrade.sh) and its use by upgrade.sh.
# Docker is simulated throughout: this is not evidence that any Qdrant release reads another's storage.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
source "$ROOT/scripts/lib/qdrant-upgrade.sh"
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT

# ── The decision, as a function ──────────────────────────────────────────────
decides() { # <name> <observation> <target> <expected word> <expected exit>
  local word code=0
  word=$(qdrant_upgrade_decision "$2" "$3") || code=$?
  if [[ "$word" != "$4" || "$code" != "$5" ]]; then
    echo "FAIL $1: '$2' -> '$3' gave '$word' (exit $code), expected '$4' (exit $5)" >&2
    exit 1
  fi
  echo "PASS decision: $1"
}
decides 'the target itself'                    'container v1.20.0'  v1.20.0 ok 0
decides 'an older patch of the target minor'   'container v1.20.0'  v1.20.3 ok 0
decides 'a newer patch of the target minor'    'container v1.20.3'  v1.20.0 newer 1
decides 'one minor below, any patch'           'container v1.19.7'  v1.20.0 ok 0
decides 'one minor below, to a later patch'    'container v1.17.4'  v1.18.2 ok 0
decides 'two minors below'                     'container v1.18.9'  v1.20.0 too_old 1
decides 'three minors below'                   'container v1.17.1'  v1.20.0 too_old 1
decides 'one minor above'                      'container v1.21.0'  v1.20.0 newer 1
decides 'a newer major'                        'container v2.0.0'   v1.20.0 newer 1
decides 'an older major'                       'container v0.11.7'  v1.0.0  too_old 1
decides 'a label without the v'                'container 1.19.2'   v1.20.0 ok 0
decides 'a target without the v'               'container v1.19.2'  1.20.0  ok 0
decides 'minors compared as numbers'           'container v1.9.0'   v1.10.0 ok 0
decides 'a container with no release label'    'container '         v1.20.0 unreadable 1
decides 'a container labelled with a tag'      'container latest'   v1.20.0 unreadable 1
decides 'a container with a partial release'   'container v1.19'    v1.20.0 unreadable 1
decides 'storage with no container'            'volume'             v1.20.0 volume 1
decides 'a fresh host'                         'fresh'              v1.20.0 fresh 0
decides 'a floating target'                    'container v1.20.0'  latest  bad_target 1
decides 'a partial target'                     'container v1.20.0'  v1.20   bad_target 1
decides 'an empty target'                      'fresh'              ''      bad_target 1

# ── The target: the env file, else the compose default, never the shell ──────
COMPOSE="$ROOT/docker-compose.example.yml"
PIN=$(qdrant_default_version "$COMPOSE")
[[ "$PIN" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "FAIL the compose file pins no full Qdrant release: '$PIN'" >&2; exit 1; }
grep -qF "image: qdrant/qdrant:\${FLEET_QDRANT_VERSION:-$PIN}" "$COMPOSE"
echo "PASS the compose file pins $PIN and the check reads the pin from it"

target() { # <name> <env content> <expected>
  printf '%s' "$2" > "$DIR/target.env"
  local got
  got=$(FLEET_QDRANT_VERSION=v9.9.9 qdrant_target_version "$DIR/target.env" "$COMPOSE")
  [[ "$got" == "$3" ]] || { echo "FAIL $1: got '$got', expected '$3'" >&2; exit 1; }
  echo "PASS target: $1"
}
target 'no key is the pin'                 $'FLEET_COMMS_ENABLED=false\n'              "$PIN"
target 'an empty key is the pin'           $'FLEET_QDRANT_VERSION=\n'                  "$PIN"
target 'a saved release wins over the pin' $'FLEET_QDRANT_VERSION=v1.18.0\n'           v1.18.0
target 'quotes and blanks are dropped'     $'FLEET_QDRANT_VERSION= "v1.18.0" \r\n'     v1.18.0
target 'no trailing newline'               'FLEET_QDRANT_VERSION=v1.19.4'              v1.19.4
[[ "$(qdrant_target_version "$DIR/absent.env" "$COMPOSE")" == "$PIN" ]]
echo 'PASS target: a missing env file is the pin'

# ── upgrade.sh, with Docker simulated ────────────────────────────────────────
mkdir -p "$DIR/scripts/lib" "$DIR/fleet" "$DIR/bin"
cp "$ROOT/setup.sh" "$ROOT/upgrade.sh" "$ROOT/docker-compose.example.yml" "$DIR/"
cp "$ROOT"/scripts/lib/*.sh "$DIR/scripts/lib/"
export CALLS="$DIR/calls" QDRANT_STATE="$DIR/qdrant-state"
# Every call is recorded with the FLEET_QDRANT_VERSION Docker was handed, which is the value
# Compose would prefer over the one saved in .env.
cat > "$DIR/bin/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s|%s\n' "${FLEET_QDRANT_VERSION-<unset>}" "$*" >> "$CALLS"
state=$(cat "$QDRANT_STATE")
case "$*" in
  info) exit 0 ;;
  'container inspect qdrant'*)
    case "$state" in 'container '*|container) printf '%s\n' "${state#container }"; exit 0 ;; esac
    exit 1 ;;
  'volume inspect'*) [[ "$state" == volume ]]; exit $? ;;
  *'ps --quiet'*) echo running ;;
  'inspect -f'*) echo healthy ;;
esac
MOCK
chmod +x "$DIR/bin/docker"
export PATH="$DIR/bin:$PATH"
ENV="$DIR/fleet/.env"

upgrade() { # <state> <env content>; sets RC and leaves output in $DIR/output
  printf '%s' "$1" > "$QDRANT_STATE"
  printf '%s' "$2" > "$ENV"
  cp "$ENV" "$DIR/before"
  : > "$CALLS"
  RC=0; bash "$DIR/upgrade.sh" > "$DIR/output" 2>&1 || RC=$?
}
refused() { # <name> <expected decision>
  [[ "$RC" == 1 ]] || { echo "FAIL $1: exit $RC" >&2; cat "$DIR/output" >&2; exit 1; }
  grep -q "this upgrade is refused ($2)" "$DIR/output" || { echo "FAIL $1: wrong refusal" >&2; cat "$DIR/output" >&2; exit 1; }
  grep -q 'Nothing was stopped or started' "$DIR/output"
  # Before any lifecycle change: nothing stopped, removed, built, pulled or started.
  ! grep -Eq '\|compose .* (down|up|rm|pull|run)( |$)|\|build ' "$CALLS"
  echo "PASS upgrade: $1"
}
proceeded() { # <name>
  [[ "$RC" == 0 ]] || { echo "FAIL $1: exit $RC" >&2; cat "$DIR/output" >&2; exit 1; }
  grep -Eq '\|compose .* down$' "$CALLS"
  grep -Eq '\|compose .* up -d$' "$CALLS"
  echo "PASS upgrade: $1"
}
BASE=$'FLEET_COMMS_ENABLED=false\n'

# The stepwise upgrade from v1.17.x: the operator saved the intermediate v1.18.0, and a shell left
# over from a later step still exports the pin. The saved release must be the one that counts: the
# check passes (v1.17 -> v1.18 is one minor; v1.17 -> v1.20 would be refused), and no Docker call,
# `up` included, is handed the exported value.
export FLEET_QDRANT_VERSION=v1.20.0
upgrade 'container v1.17.4' "${BASE}FLEET_QDRANT_VERSION=v1.18.0"$'\n'
proceeded 'an exported v1.20.0 does not replace the saved v1.18.0 (from v1.17.4)'
if grep -v '^<unset>|' "$CALLS" | grep -q .; then
  echo 'FAIL Docker was handed an exported FLEET_QDRANT_VERSION:' >&2
  grep -v '^<unset>|' "$CALLS" | head -3 >&2
  exit 1
fi
grep -qx 'FLEET_QDRANT_VERSION=v1.18.0' "$ENV"
echo 'PASS upgrade: every Docker call ran without the exported release, and .env still says v1.18.0'
# The reverse: an exported intermediate must not hide a saved jump either.
export FLEET_QDRANT_VERSION=v1.18.0
upgrade 'container v1.17.4' "${BASE}FLEET_QDRANT_VERSION=v1.20.0"$'\n'
refused 'an exported v1.18.0 does not excuse a saved v1.20.0 (from v1.17.4)' too_old
unset FLEET_QDRANT_VERSION

upgrade 'container v1.17.1' "$BASE"
refused 'three minors below the pin' too_old
grep -q 'Qdrant on this host is v1.17.1' "$DIR/output"

upgrade 'container v1.16.0' "${BASE}FLEET_QDRANT_VERSION=v1.18.0"$'\n'
refused 'setting FLEET_QDRANT_VERSION is not a way past the check' too_old

upgrade 'container v1.21.0' "$BASE"
refused 'a host newer than the pin is not downgraded' newer
grep -q 'FLEET_QDRANT_VERSION=v1.21.0' "$DIR/output"

upgrade 'container v1.20.1' "$BASE"
refused 'a newer patch of the pinned minor is not downgraded' newer

upgrade 'container v1.21.0' "${BASE}FLEET_QDRANT_VERSION=v1.21.0"$'\n'
proceeded 'a host newer than the pin stays where .env says'

upgrade 'container ' "$BASE"
refused 'a container whose release cannot be read' unreadable

upgrade 'volume' "$BASE"
refused 'storage with no container to read' volume
grep -q 'docker volume rm fleet_qdrant_data' "$DIR/output"

upgrade 'container v1.20.0' "${BASE}FLEET_QDRANT_VERSION=latest"$'\n'
refused 'a floating FLEET_QDRANT_VERSION' bad_target

upgrade 'fresh' "$BASE"
proceeded 'a host with no Qdrant container or storage is not blocked'

upgrade 'container v1.19.2' "$BASE"
proceeded 'one minor below the pin'
grep -q 'Ports 7233, 8080, 3100 and 3300' "$DIR/output"
echo 'PASS upgrade: says the four ports are loopback-only'

upgrade 'container v1.20.0' "${BASE}FLEET_INTERNAL_BIND_ADDRESS=0.0.0.0"$'\n'
proceeded 'the pinned release itself'
! grep -q 'Ports 7233, 8080, 3100 and 3300' "$DIR/output"
echo 'PASS upgrade: says nothing about the ports once .env records a choice'

# setup.sh drops the export too, before it reads anything or calls Docker.
for script in setup.sh upgrade.sh; do
  unset_line=$(grep -n '^unset FLEET_QDRANT_VERSION$' "$ROOT/$script" | head -1 | cut -d: -f1)
  first_docker=$(grep -nE '(^|[^#]*[ (!])docker ' "$ROOT/$script" | grep -vE '^[0-9]+:[[:space:]]*#' | head -1 | cut -d: -f1)
  [[ -n "$unset_line" && -n "$first_docker" && "$unset_line" -lt "$first_docker" ]] \
    || { echo "FAIL $script does not unset FLEET_QDRANT_VERSION before its first Docker call" >&2; exit 1; }
done
echo 'PASS setup.sh and upgrade.sh unset FLEET_QDRANT_VERSION before their first Docker call'
