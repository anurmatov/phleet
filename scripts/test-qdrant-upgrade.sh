#!/usr/bin/env bash
# The Qdrant release check (scripts/lib/qdrant-upgrade.sh) and its use by upgrade.sh and setup.sh.
#
# Docker's lifecycle is simulated: nothing is stopped, started, pulled or built, and this is not
# evidence that any Qdrant release reads another's storage. `docker compose config` is NOT
# simulated: every target below is resolved by the real Compose on this machine, because a
# simulated answer cannot show that the check and Compose agree.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
source "$ROOT/scripts/lib/qdrant-upgrade.sh"
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
REAL_DOCKER=$(command -v docker) || { echo 'FAIL docker with the compose plugin is required' >&2; exit 1; }
"$REAL_DOCKER" compose version >/dev/null 2>&1 || { echo 'FAIL docker compose is required' >&2; exit 1; }
echo "Compose: $("$REAL_DOCKER" compose version --short 2>/dev/null || "$REAL_DOCKER" compose version)"

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
decides 'a floating target on a fresh host'    'fresh'              latest  bad_target 1
decides 'an unknown target, container present' 'container v1.20.0'  ''      no_target 1
decides 'an unknown target, storage present'   'volume'             ''      no_target 1
decides 'an unknown target on a fresh host'    'fresh'              ''      fresh 0

# ── A tree the real scripts and the real Compose can run in ──────────────────
mkdir -p "$DIR/scripts/lib" "$DIR/fleet" "$DIR/bin"
cp "$ROOT/setup.sh" "$ROOT/upgrade.sh" "$ROOT/docker-compose.example.yml" "$DIR/"
cp "$ROOT"/scripts/lib/*.sh "$DIR/scripts/lib/"
COMPOSE_EXAMPLE="$DIR/docker-compose.example.yml"
PIN=$(sed -n 's/^ *image: qdrant\/qdrant:\${FLEET_QDRANT_VERSION:-\(v[0-9]*\.[0-9]*\.[0-9]*\)}$/\1/p' "$COMPOSE_EXAMPLE")
[[ -n "$PIN" ]] || { echo 'FAIL the compose file pins no full Qdrant release' >&2; exit 1; }
echo "PASS the compose file pins Qdrant $PIN"
ENV="$DIR/fleet/.env"
# The generated definition, made the way both scripts make it.
EXPECTED_COMPOSE="$DIR/fleet/.expected-compose.yml"
sed -E -e 's|^(      context: )\.$|\1..|' -e 's|\./deploy/comms-seaweedfs:|../deploy/comms-seaweedfs:|' \
  "$COMPOSE_EXAMPLE" > "$EXPECTED_COMPOSE"

write_env() { # <qdrant lines>
  cp "$ROOT/.env.example" "$ENV"
  printf 'FLEET_BASE_DIR=%s\nFLEET_COMMS_ENABLED=false\n%s' "$DIR/fleet" "$1" >> "$ENV"
}
# What Compose itself resolves for this .env, asked directly and without the function under test.
# The shell's own FLEET_QDRANT_VERSION is removed, as both scripts remove it.
compose_says() {
  (cd "$DIR/fleet" && env -u FLEET_QDRANT_VERSION "$REAL_DOCKER" compose -p fleet -f "$EXPECTED_COMPOSE" \
    --env-file .env config --images qdrant) | sed -n 's|^qdrant/qdrant:||p'
}

# ── The target is Compose's answer, for every way .env can spell it ──────────
agrees() { # <name> <qdrant lines> <the release Compose is known to choose>
  write_env "$2"
  local asked direct
  direct=$(compose_says)
  asked=$(qdrant_target_version "$DIR/fleet" fleet "$EXPECTED_COMPOSE")
  [[ -n "$direct" && "$asked" == "$direct" ]] \
    || { echo "FAIL $1: the check would use '$asked', Compose resolves '$direct'" >&2; exit 1; }
  [[ "$direct" == "$3" ]] \
    || { echo "FAIL $1: this Compose resolves '$direct', where '$3' was expected" >&2; exit 1; }
  echo "PASS target: $1 -> $direct, as Compose resolves it"
}
agrees 'no key'                          ''                                                              "$PIN"
agrees 'an empty key'                    $'FLEET_QDRANT_VERSION=\n'                                      "$PIN"
agrees 'one key'                         $'FLEET_QDRANT_VERSION=v1.18.0\n'                               v1.18.0
agrees 'the key twice, the pin last'     $'FLEET_QDRANT_VERSION=v1.18.0\nFLEET_QDRANT_VERSION=v1.20.0\n' v1.20.0
agrees 'the key twice, the step last'    $'FLEET_QDRANT_VERSION=v1.20.0\nFLEET_QDRANT_VERSION=v1.18.0\n' v1.18.0
agrees 'a leading blank'                 $' FLEET_QDRANT_VERSION=v1.18.0\n'                              v1.18.0
agrees 'an export prefix'                $'export FLEET_QDRANT_VERSION=v1.18.0\n'                        v1.18.0
agrees 'quotes and a trailing comment'   $'FLEET_QDRANT_VERSION="v1.18.0" # step\n'                      v1.18.0
agrees 'no trailing newline'             'FLEET_QDRANT_VERSION=v1.19.4'                                  v1.19.4
write_env ''
[[ -z "$(qdrant_target_version "$DIR/fleet" fleet "$DIR/fleet/absent.yml")" ]]
echo 'PASS target: a definition Compose cannot read gives no target'

# ── Docker, with the lifecycle simulated and `compose config` real ───────────
export CALLS="$DIR/calls" QDRANT_STATE="$DIR/qdrant-state" REAL_DOCKER
# Every call is recorded with the FLEET_QDRANT_VERSION Docker was handed, which is the value
# Compose would prefer over the one saved in .env.
cat > "$DIR/bin/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s|%s\n' "${FLEET_QDRANT_VERSION-<unset>}" "$*" >> "$CALLS"
state=$(cat "$QDRANT_STATE")
case "$*" in
  info) exit 0 ;;
  'compose '*' config --images qdrant')
    [[ "${COMPOSE_CONFIG_FAILS:-false}" != true ]] || exit 1
    exec "$REAL_DOCKER" "$@" ;;
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
LIFECYCLE='\|compose .* (down|up|rm|pull|run)( |$)|\|build '

# upgrade.sh, the whole script.
upgrade() { # <state> <qdrant lines>
  printf '%s' "$1" > "$QDRANT_STATE"
  write_env "$2"
  : > "$CALLS"
  RC=0; bash "$DIR/upgrade.sh" > "$DIR/output" 2>&1 || RC=$?
  CALLER=upgrade.sh
}
# setup.sh's own check: the lines between its two markers, run with the variables setup.sh has at
# that point and against the definition it has just generated, followed by a stand-in for its `up`.
SETUP_CHECK="$DIR/setup-check.sh"
{
  printf '%s\n' 'set -euo pipefail' 'source "$SCRIPT_DIR/scripts/lib/qdrant-upgrade.sh"' \
    'ok() { echo "OK $*"; }' 'fail() { echo "FAIL $*"; }' 'COMMS_PROFILE_ARGS=()'
  sed -n '/# qdrant-check:begin/,/# qdrant-check:end/p' "$ROOT/setup.sh"
  printf '%s\n' '(cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env "${COMMS_PROFILE_ARGS[@]}" up -d)'
} > "$SETUP_CHECK"
grep -q 'qdrant_target_version "$FLEET_BASE_DIR" "$COMPOSE_PROJECT" "$COMPOSE_FILE" "${COMMS_PROFILE_ARGS\[@\]}"' "$SETUP_CHECK" \
  || { echo 'FAIL setup.sh does not ask Compose with the arguments of its own up' >&2; exit 1; }
check_line=$(grep -n '# qdrant-check:end' "$ROOT/setup.sh" | cut -d: -f1)
up_line=$(grep -n '"${COMMS_PROFILE_ARGS\[@\]}" up -d)' "$ROOT/setup.sh" | cut -d: -f1)
[[ -n "$check_line" && -n "$up_line" && "$check_line" -lt "$up_line" ]] \
  || { echo 'FAIL setup.sh does not make the Qdrant check before its up' >&2; exit 1; }
echo 'PASS setup.sh makes the check before its up, with the arguments of that up'
setup() { # <state> <qdrant lines>
  printf '%s' "$1" > "$QDRANT_STATE"
  write_env "$2"
  cp "$EXPECTED_COMPOSE" "$DIR/fleet/docker-compose.yml"
  : > "$CALLS"
  RC=0
  SCRIPT_DIR="$DIR" FLEET_BASE_DIR="$DIR/fleet" COMPOSE_PROJECT=fleet ENV_FILE="$ENV" \
    COMPOSE_EXAMPLE="$COMPOSE_EXAMPLE" COMPOSE_FILE="$DIR/fleet/docker-compose.yml" \
    bash "$SETUP_CHECK" > "$DIR/output" 2>&1 || RC=$?
  CALLER=setup.sh
}

# The target the script reported must be the one Compose resolves for the same .env.
same_target_as_compose() { # <name>
  local reported direct
  reported=$(sed -n 's/.*Compose target: \([^ ]*\).*/\1/p' "$DIR/output" | head -1)
  direct=$(compose_says)
  [[ -n "$direct" && "$reported" == "$direct" ]] \
    || { echo "FAIL $CALLER: $1: the check used '$reported', Compose resolves '$direct'" >&2; cat "$DIR/output" >&2; exit 1; }
}
refused() { # <name> <expected decision>
  [[ "$RC" == 1 ]] || { echo "FAIL $CALLER: $1: exit $RC" >&2; cat "$DIR/output" >&2; exit 1; }
  grep -q "refused ($2)" "$DIR/output" || { echo "FAIL $CALLER: $1: wrong refusal" >&2; cat "$DIR/output" >&2; exit 1; }
  grep -q 'Nothing was stopped or started' "$DIR/output"
  # Before any lifecycle change: nothing stopped, removed, built, pulled or started.
  ! grep -Eq "$LIFECYCLE" "$CALLS"
  [[ "$2" == no_target ]] || same_target_as_compose "$1"
  echo "PASS $CALLER: $1"
}
proceeded() { # <name>
  [[ "$RC" == 0 ]] || { echo "FAIL $CALLER: $1: exit $RC" >&2; cat "$DIR/output" >&2; exit 1; }
  grep -Eq '\|compose .* up -d$' "$CALLS"
  [[ "$CALLER" == setup.sh ]] || grep -Eq '\|compose .* down$' "$CALLS"
  [[ "${COMPOSE_CONFIG_FAILS:-false}" == true ]] || same_target_as_compose "$1"
  echo "PASS $CALLER: $1"
}

for caller in upgrade setup; do
  # The three ways a second reading of .env went wrong. In each, what Compose would start is not
  # what the first FLEET_QDRANT_VERSION= line says, and the check must follow Compose.
  $caller 'container v1.17.4' $'FLEET_QDRANT_VERSION=v1.18.0\nFLEET_QDRANT_VERSION=v1.20.0\n'
  refused 'the key twice: Compose takes the later v1.20.0, three minors above v1.17.4' too_old
  $caller 'container v1.19.2' $' FLEET_QDRANT_VERSION=v1.18.0\n'
  refused 'a leading blank: Compose takes v1.18.0, a downgrade from v1.19.2' newer
  $caller 'container v1.19.2' $'export FLEET_QDRANT_VERSION=v1.18.0\n'
  refused 'an export prefix: Compose takes v1.18.0, a downgrade from v1.19.2' newer
  # And the same spellings where the move Compose would make is allowed.
  $caller 'container v1.17.4' $'FLEET_QDRANT_VERSION=v1.20.0\nFLEET_QDRANT_VERSION=v1.18.0\n'
  proceeded 'the key twice: Compose takes the later v1.18.0, one minor above v1.17.4'
  $caller 'container v1.17.4' $' FLEET_QDRANT_VERSION=v1.18.0\n'
  proceeded 'a leading blank, one minor above v1.17.4'
  $caller 'container v1.17.4' $'export FLEET_QDRANT_VERSION="v1.18.0" # step\n'
  proceeded 'an export prefix with quotes and a comment, one minor above v1.17.4'

  $caller 'container v1.17.1' ''
  refused 'three minors below the pin' too_old
  grep -q 'Qdrant on this host is v1.17.1' "$DIR/output"
  $caller 'container v1.16.0' $'FLEET_QDRANT_VERSION=v1.18.0\n'
  refused 'setting FLEET_QDRANT_VERSION is not a way past the check' too_old
  $caller 'container v1.21.0' ''
  refused 'a host newer than the pin is not downgraded' newer
  grep -q 'FLEET_QDRANT_VERSION=v1.21.0' "$DIR/output"
  $caller 'container v1.20.1' ''
  refused 'a newer patch of the pinned minor is not downgraded' newer
  $caller 'container v1.21.0' $'FLEET_QDRANT_VERSION=v1.21.0\n'
  proceeded 'a host newer than the pin stays where .env says'
  $caller 'container ' ''
  refused 'a container whose release cannot be read' unreadable
  $caller 'volume' ''
  refused 'storage with no container to read' volume
  grep -q 'docker volume rm fleet_qdrant_data' "$DIR/output"
  $caller 'container v1.20.0' $'FLEET_QDRANT_VERSION=latest\n'
  refused 'a floating FLEET_QDRANT_VERSION' bad_target
  $caller 'fresh' ''
  proceeded 'a host with no Qdrant container or storage is not blocked'
  $caller 'container v1.19.2' ''
  proceeded 'one minor below the pin'

  # Compose cannot be asked: refused where there is Qdrant state, not on a fresh host.
  export COMPOSE_CONFIG_FAILS=true
  $caller 'container v1.20.0' ''
  refused 'Compose gives no target and a container exists' no_target
  $caller 'volume' ''
  refused 'Compose gives no target and storage exists' no_target
  $caller 'fresh' ''
  proceeded 'Compose gives no target on a fresh host'
  unset COMPOSE_CONFIG_FAILS
done

# ── The invoking shell does not decide (upgrade.sh, the whole script) ────────
# The stepwise upgrade from v1.17.x: the operator saved the intermediate v1.18.0, and a shell left
# over from a later step still exports the pin. No Docker call, `compose config` and `up` included,
# may be handed the exported value, so Compose resolves the saved one.
export FLEET_QDRANT_VERSION=v1.20.0
upgrade 'container v1.17.4' $'FLEET_QDRANT_VERSION=v1.18.0\n'
proceeded 'an exported v1.20.0 does not replace the saved v1.18.0 (from v1.17.4)'
if grep -v '^<unset>|' "$CALLS" | grep -q .; then
  echo 'FAIL Docker was handed an exported FLEET_QDRANT_VERSION:' >&2
  grep -v '^<unset>|' "$CALLS" | head -3 >&2
  exit 1
fi
grep -q 'Compose target: v1.18.0' "$DIR/output"
echo 'PASS upgrade.sh: every Docker call ran without the exported release, and the target was v1.18.0'
# The reverse: an exported intermediate must not hide a saved jump either.
export FLEET_QDRANT_VERSION=v1.18.0
upgrade 'container v1.17.4' $'FLEET_QDRANT_VERSION=v1.20.0\n'
refused 'an exported v1.18.0 does not excuse a saved v1.20.0 (from v1.17.4)' too_old
unset FLEET_QDRANT_VERSION

# setup.sh drops the export too, before it reads anything or calls Docker.
for script in setup.sh upgrade.sh; do
  unset_line=$(grep -n '^unset FLEET_QDRANT_VERSION$' "$ROOT/$script" | head -1 | cut -d: -f1)
  first_docker=$(grep -nE '(^|[^#]*[ (!])docker ' "$ROOT/$script" | grep -vE '^[0-9]+:[[:space:]]*#' | head -1 | cut -d: -f1)
  [[ -n "$unset_line" && -n "$first_docker" && "$unset_line" -lt "$first_docker" ]] \
    || { echo "FAIL $script does not unset FLEET_QDRANT_VERSION before its first Docker call" >&2; exit 1; }
done
echo 'PASS setup.sh and upgrade.sh unset FLEET_QDRANT_VERSION before their first Docker call'

# ── The port notice ──────────────────────────────────────────────────────────
upgrade 'fresh' ''
grep -q 'Ports 7233, 8080, 3100 and 3300' "$DIR/output"
echo 'PASS upgrade.sh: says the four ports are loopback-only by default'
# Any assignment Compose would read is a recorded choice: the notice must not say "loopback" over it.
for line in 'FLEET_INTERNAL_BIND_ADDRESS=0.0.0.0' ' FLEET_INTERNAL_BIND_ADDRESS=0.0.0.0' 'export FLEET_INTERNAL_BIND_ADDRESS=0.0.0.0'; do
  upgrade 'fresh' "$line"$'\n'
  [[ "$RC" == 0 ]]
  ! grep -q 'Ports 7233, 8080, 3100 and 3300' "$DIR/output"
done
echo 'PASS upgrade.sh: says nothing about the ports once .env assigns the address, however it is spelled'
