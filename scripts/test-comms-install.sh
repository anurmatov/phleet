#!/usr/bin/env bash
# Script-level regression fixtures. These are NOT cold-host or object-preservation evidence.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/scripts/lib" "$DIR/fleet" "$DIR/bin"
cp "$ROOT/setup.sh" "$ROOT/upgrade.sh" "$DIR/"
cp "$ROOT/scripts/lib/comms-profiles.sh" "$ROOT/scripts/lib/stock-managed-preflight.sh" "$DIR/scripts/lib/"
cp "$ROOT/docker-compose.example.yml" "$DIR/"
# upgrade.sh runs the #492 managed-runtime rollout guard against the orchestrator Dockerfile.
mkdir -p "$DIR/src/Fleet.Orchestrator"
cp "$ROOT/scripts/check-stock-runtime-rollout.sh" "$ROOT/scripts/check-stock-gateway-backup.sh" "$DIR/scripts/"
cp "$ROOT/src/Fleet.Orchestrator/Dockerfile" "$DIR/src/Fleet.Orchestrator/"
export CALLS="$DIR/calls"
cat > "$DIR/bin/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CALLS"
case "$*" in
  info) exit 0 ;;
  'image inspect minio/'*) exit 1 ;;
  *'ps --quiet'*) echo running ;;
  'inspect -f'*) echo healthy ;;
  'run --rm --entrypoint openshell '*) printf '%s\n' "${STUB_CLI_VERSION-openshell 0.1.5}" ;;
esac
MOCK
chmod +x "$DIR/bin/docker"
export PATH="$DIR/bin:$PATH"
ENV="$DIR/fleet/.env"
# No Docker call from --comms; a recorded true is never rewritten or re-asked.
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=true
FLEET_COMMS_BIND=127.0.0.1:3500
FLEET_COMMS_TRUST_PROXY=false
FLEET_COMMS_CONVERSATIONS_ENABLED=false
ENV
cp "$ENV" "$DIR/before"
printf 'n\n' | bash "$DIR/setup.sh" --comms > "$DIR/output"
cmp "$ENV" "$DIR/before"
[[ ! -f "$CALLS" ]]
grep -q 'Run ./upgrade.sh to apply.' "$DIR/output"
echo 'PASS --comms only asks declined decisions and starts nothing'
cp "$ENV" "$DIR/before"
printf 'n\n' | bash "$DIR/setup.sh" --comms --dry-run > "$DIR/output"
cmp "$ENV" "$DIR/before"
echo 'PASS --comms dry-run preserves file'
# A legacy endpoint with no store must resolve the previewed backfill, not the real file.
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=true
FLEET_COMMS_BIND=127.0.0.1:3500
FLEET_COMMS_TRUST_PROXY=false
FLEET_COMMS_CONVERSATIONS_ENABLED=false
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000
ENV
cp "$ENV" "$DIR/before"
printf 'n\n' | bash "$DIR/setup.sh" --comms --dry-run > "$DIR/output"
cmp "$ENV" "$DIR/before"
grep -q 'Media store: minio' "$DIR/output"
[[ ! -f "$CALLS" ]]
echo 'PASS legacy dry-run resolves previewed backfill without writes or Docker'
if bash "$DIR/setup.sh" --comms --skip-services > "$DIR/output" 2>&1; then exit 1; fi
echo 'PASS --comms rejects other flags'
# Missing withdrawn images fail before down, rm or up.
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=true
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000
ENV
: > "$CALLS"
rc=0; bash "$DIR/upgrade.sh" > "$DIR/output" 2>&1 || rc=$?
[[ "$rc" == 1 ]]
grep -q 'legacy MinIO media images are not on this host' "$DIR/output"
! grep -Eq ' down$| rm | up ' "$CALLS"
! grep -q ' pull ' "$CALLS"
grep -qx 'FLEET_COMMS_MEDIA_STORE=minio' "$ENV"
echo 'PASS missing legacy images fail before lifecycle changes'
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=true
FLEET_COMMS_MEDIA_ENDPOINT=http://store
FLEET_COMMS_MEDIA_STORE=invalid
ENV
: > "$CALLS"
rc=0; bash "$DIR/upgrade.sh" > "$DIR/output" 2>&1 || rc=$?
[[ "$rc" == 2 ]]
! grep -q '^compose ' "$CALLS"
echo 'PASS invalid store fails before compose'
# Comms disabled, even with an endpoint: remove every optional service but keep keys/volumes.
# The epic watcher/grant keys are present, so upgrade has nothing to add and must not touch .env.
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=false
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000
FLEET_COMMS_MEDIA_STORE=minio
FLEET_COMMS_MEDIA_SECRET_KEY=preserve-secret
FLEET_EPIC_WATCHER_ENABLED=false
FLEET_EPIC_WATCHER_INTERVAL_SECONDS=300
FLEET_EPIC_GRANTS_ENABLED=false
FLEET_EPIC_GRANTS_MAX_DAYS=14
FLEET_EPIC_GRANTS_DENIED_REPOS=
FLEET_AUTONOMY_ENABLED=false
FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false
FLEET_STOCK_MANAGED_ENABLED=false
ENV
cp "$ENV" "$DIR/before"
: > "$CALLS"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1
cmp "$ENV" "$DIR/before"
rmline=$(grep ' rm -sf ' "$CALLS")
for service in fleet-comms fleet-comms-ops comms-mysql comms-minio comms-minio-init comms-seaweedfs comms-seaweedfs-init; do
  [[ " $rmline " == *" $service "* ]]
done
! grep -Eq ' down .* -v| rm .* -v|image inspect| pull |fleet:comms' "$CALLS"
echo 'PASS disabled Comms removes containers but preserves keys and volumes'
# #492 review W5: the unconditional orchestrator backup bind source exists before compose runs, as
# the operator and 0700, so Docker never creates it root-owned on a default-off install.
[[ -d "$DIR/fleet/openshell-backup" && "$(ls -ld "$DIR/fleet/openshell-backup" | cut -c1-10)" == drwx------ ]]
[[ -O "$DIR/fleet/openshell-backup" ]]
echo 'PASS default-off upgrade pre-creates the backup bind source 0700'
# Epic watcher/grant upgrades append missing defaults, keep a present value, and never
# glues a key onto a last line that has no newline.
printf 'FLEET_COMMS_ENABLED=false\nFLEET_EPIC_GRANTS_MAX_DAYS=7' > "$ENV"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1
[[ "$(cat "$ENV")" == "$(printf 'FLEET_COMMS_ENABLED=false\nFLEET_EPIC_GRANTS_MAX_DAYS=7\nFLEET_EPIC_WATCHER_ENABLED=false\nFLEET_EPIC_WATCHER_INTERVAL_SECONDS=300\nFLEET_EPIC_GRANTS_ENABLED=false\nFLEET_EPIC_GRANTS_DENIED_REPOS=\nFLEET_AUTONOMY_ENABLED=false\nFLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false\nFLEET_STOCK_MANAGED_ENABLED=false')" ]]
grep -q '^FLEET_STOCK_CONFIG_DIR_UID=' "$ENV" && exit 1 # review C1: default-off upgrade never adds the managed owner key
echo 'PASS upgrade adds missing epic watcher/grant keys with defaults and keeps present values'
sed -i.bak 's/^FLEET_AUTONOMY_ENABLED=false$/FLEET_AUTONOMY_ENABLED=true/' "$ENV"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1
[[ "$(grep -c '^FLEET_AUTONOMY_ENABLED=' "$ENV")" == 1 ]]
grep -qx 'FLEET_AUTONOMY_ENABLED=true' "$ENV"
echo 'PASS upgrade preserves existing autonomy values'

# Pull failure must also precede every lifecycle operation.
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=true
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-seaweedfs:8333
FLEET_COMMS_MEDIA_STORE=seaweedfs
ENV
sed -i.bak '/case "\$\*" in/a\  *" pull "*) exit 1 ;;' "$DIR/bin/docker"
: > "$CALLS"
rc=0; bash "$DIR/upgrade.sh" > "$DIR/output" 2>&1 || rc=$?
[[ "$rc" == 1 ]]
grep -q ' pull comms-seaweedfs' "$CALLS"
! grep -Eq ' down$| rm | up ' "$CALLS"
echo 'PASS pinned image pull failure leaves lifecycle untouched'
# A fresh full dry-run has no .env yet. Preview from the example without creating runtime state.
FRESH="$DIR/fresh"
mkdir -p "$FRESH/scripts/lib" "$DIR/home/.gemini" "$DIR/previews"
cp "$ROOT/setup.sh" "$ROOT/.env.example" "$ROOT/docker-compose.example.yml" "$FRESH/"
cp "$ROOT/scripts/lib/comms-profiles.sh" "$ROOT/scripts/lib/stock-managed-preflight.sh" "$FRESH/scripts/lib/"
printf '%s\n' '{"refresh_token":"synthetic-fixture-token"}' > "$DIR/home/.gemini/oauth_creds.json"
cp "$FRESH/.env.example" "$DIR/example-before"
: > "$CALLS"
if ! printf '3\nn\n' | HOME="$DIR/home" TMPDIR="$DIR/previews" bash "$FRESH/setup.sh" --dry-run > "$DIR/output" 2>&1; then
  cat "$DIR/output" >&2; exit 1
fi
# No runtime dir at all, so neither the client nor the backup bind source (#492 W5) is pre-created.
[[ ! -e "$FRESH/fleet" && ! -e "$FRESH/.env" ]]
cmp "$FRESH/.env.example" "$DIR/example-before"
[[ -z "$(ls -A "$DIR/previews")" ]]
! grep -Eq '^build |^compose .* (pull|up|down|rm|run)|^network create ' "$CALLS"
echo 'PASS fresh full dry-run exits zero without real config or leftover previews'
# --skip-build still replaces the orchestrator, so the rollout guard must run before the build branch.
guard_line=$(grep -n 'check-stock-runtime-rollout.sh' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
skip_line=$(grep -n 'warn "Skipping image builds (--skip-build)"' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
[[ -n "$guard_line" && -n "$skip_line" && "$guard_line" -lt "$skip_line" ]]
echo 'PASS setup.sh runs the rollout guard regardless of --skip-build'
# #492 F8: an enabled managed runtime refuses incomplete trust before any lifecycle change.
sed -i.bak '/ pull "\*) exit 1 ;;/d' "$DIR/bin/docker"
mkdir -p "$DIR/nosqlite"
printf '#!/usr/bin/env bash\nexit 1\n' > "$DIR/nosqlite/python3"
chmod +x "$DIR/nosqlite/python3"
CLIENT="$DIR/fleet/openshell-client"
MTLS="$CLIENT/openshell/gateways/fixture/mtls"
BACKUPS="$DIR/gateway-backups"
managed_fixture() {
  cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=false
FLEET_EPIC_WATCHER_ENABLED=false
FLEET_EPIC_WATCHER_INTERVAL_SECONDS=300
FLEET_EPIC_GRANTS_ENABLED=false
FLEET_EPIC_GRANTS_MAX_DAYS=14
FLEET_EPIC_GRANTS_DENIED_REPOS=
FLEET_AUTONOMY_ENABLED=false
FLEET_STOCK_MANAGED_ENABLED=true
FLEET_STOCK_GATEWAY_IMAGE=gateway.example.test/openshell@sha256:0000
FLEET_DOCKER_GID=999
FLEET_STOCK_BROKER_MANAGEMENT_URL=https://broker.example.test:15671
FLEET_STOCK_BROKER_ADMIN_USER_REF=FIXTURE_BROKER_USER
FLEET_STOCK_BROKER_ADMIN_PASSWORD_REF=FIXTURE_BROKER_PASSWORD
FIXTURE_BROKER_USER=fixture-admin
FIXTURE_BROKER_PASSWORD=fixture-password
OPENSHELL_CLI_URL=https://cli.example.test/openshell-0.1.5
OPENSHELL_CLI_SHA256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
ENV
  echo "FLEET_STOCK_BACKUP_DIR=$BACKUPS" >> "$ENV"
  rm -rf "$BACKUPS"; mkdir -p "$BACKUPS"
  date -u +%Y-%m-%dT%H:%M:%SZ > "$BACKUPS/last-success"
  mkdir -p "$BACKUPS/gateway-20260101T000000Z"; : > "$BACKUPS/gateway-20260101T000000Z/COMPLETE"; : > "$BACKUPS/gateway-20260101T000000Z/SHA256SUMS"
  rm -rf "$CLIENT"; mkdir -p "$MTLS"
  echo fixture-ca > "$CLIENT/broker-ca.pem"
  echo fixture-cert > "$MTLS/tls.crt"
  echo fixture-key > "$MTLS/tls.key"
  : > "$CALLS"
}
managed_refuses() {
  local why="$1" rc=0
  PATH="${2:-$PATH}" bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || rc=$?
  [[ "$rc" == 1 ]] || { cat "$DIR/output" >&2; exit 1; }
  grep -q "managed runtime preflight: .*$why" "$DIR/output"
  if grep -Eq ' down$| rm | up ' "$CALLS"; then exit 1; fi
  echo "PASS managed upgrade refuses: $why"
}
managed_fixture; chmod 750 "$BACKUPS"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; exit 1; }
if grep -q 'managed runtime preflight' "$DIR/output"; then exit 1; fi
echo 'PASS managed upgrade with complete trust passes the preflight'
# Review C1: only an enabled managed upgrade records the authorized owner of openshell-client.
grep -qx "FLEET_STOCK_CONFIG_DIR_UID=$(id -u)" "$ENV"
echo 'PASS managed upgrade records the openshell-client owner uid'
# #492 A3: the orchestrator build gets the pinned stock CLI and the built image is checked for it.
grep -q 'build -f .*/src/Fleet.Orchestrator/Dockerfile --build-arg OPENSHELL_CLI_URL=https://cli.example.test/openshell-0.1.5 --build-arg OPENSHELL_CLI_SHA256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef -t fleet:orchestrator ' "$CALLS"
grep -qx 'run --rm --entrypoint openshell fleet:orchestrator --version' "$CALLS"
echo 'PASS managed upgrade builds the orchestrator with the pinned stock CLI and checks it'
# #492 review W5: an existing configured backup dir keeps its mode; upgrade only creates an absent one.
[[ "$(ls -ld "$BACKUPS" | cut -c1-10)" == drwxr-x--- ]]
echo 'PASS upgrade leaves an existing backup bind source untouched'
managed_fixture; sed -i.bak 's|^FLEET_STOCK_BROKER_MANAGEMENT_URL=https:|FLEET_STOCK_BROKER_MANAGEMENT_URL=http:|' "$ENV"
managed_refuses 'must be an https://'
managed_fixture; rm "$CLIENT/broker-ca.pem"
managed_refuses 'broker CA'
managed_fixture; sed -i.bak '/^FIXTURE_BROKER_PASSWORD=/d' "$ENV"
managed_refuses 'FLEET_STOCK_BROKER_ADMIN_PASSWORD_REF must name'
managed_fixture; rm "$MTLS/tls.key"
managed_refuses 'mTLS client cert/key'
managed_fixture
managed_refuses 'sqlite3' "$DIR/nosqlite:$PATH"
managed_fixture; sed -i.bak '/^OPENSHELL_CLI_URL=/d' "$ENV"
managed_refuses 'OPENSHELL_CLI_URL must be an https://'
managed_fixture; sed -i.bak 's|^OPENSHELL_CLI_URL=https:|OPENSHELL_CLI_URL=http:|' "$ENV"
managed_refuses 'OPENSHELL_CLI_URL must be an https://'
managed_fixture; sed -i.bak 's|^OPENSHELL_CLI_SHA256=.*|OPENSHELL_CLI_SHA256=abc123|' "$ENV"
managed_refuses 'OPENSHELL_CLI_SHA256 its 64-hex'
# #492 A3: a built image without the pinned CLI version is never started.
for cli in '' 'openshell 0.1.4'; do
  managed_fixture; rc=0
  STUB_CLI_VERSION="$cli" bash "$DIR/upgrade.sh" > "$DIR/output" 2>&1 || rc=$?
  [[ "$rc" == 1 ]] || { cat "$DIR/output" >&2; exit 1; }
  grep -q 'managed runtime preflight: image fleet:orchestrator lacks the stock CLI openshell 0.1.5' "$DIR/output"
  if grep -Eq ' up( |$)' "$CALLS"; then exit 1; fi
  echo "PASS managed upgrade refuses to start an orchestrator image with CLI [${cli:-none}]"
done
# #492 §16: a missing, failed or stale gateway backup refuses an enabled managed upgrade.
managed_fixture; sed -i.bak '/^FLEET_STOCK_BACKUP_DIR=/d' "$ENV"
managed_refuses 'FLEET_STOCK_BACKUP_DIR must name'
managed_fixture; rm "$BACKUPS/last-success"
managed_refuses 'gateway backup STALE: no successful gateway backup'
managed_fixture; echo 2020-01-01T00:00:00Z > "$BACKUPS/last-success"
managed_refuses 'gateway backup STALE: last gateway backup is [0-9]*h old'
managed_fixture; cp "$BACKUPS/last-success" "$BACKUPS/last-failure"
managed_refuses 'gateway backup STALE: the latest gateway backup run failed'
# Default-off: none of that trust exists and python3 lacks sqlite3, yet nothing changes.
managed_fixture; rm -rf "$CLIENT"
sed -i.bak -e 's/^FLEET_STOCK_MANAGED_ENABLED=true$/FLEET_STOCK_MANAGED_ENABLED=false/' -e '/^FLEET_STOCK_BROKER/d' "$ENV"
PATH="$DIR/nosqlite:$PATH" bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; exit 1; }
if grep -q 'managed runtime preflight' "$DIR/output"; then exit 1; fi
if grep -Eq 'OPENSHELL_CLI|entrypoint openshell' "$CALLS"; then exit 1; fi
grep -q 'build -f .*/src/Fleet.Orchestrator/Dockerfile -t fleet:orchestrator ' "$CALLS"
echo 'PASS default-off upgrade skips the managed preflight, CLI build args and CLI check'
# #492 A6: the flag is parsed like the services' .NET bool binder (case-insensitive, quotes and
# whitespace stripped). True, "true" and TRUE are enabled and run the preflight; false and empty skip it.
set_managed_flag() { grep -v '^FLEET_STOCK_MANAGED_ENABLED=' "$ENV" > "$ENV.tmp"; printf 'FLEET_STOCK_MANAGED_ENABLED=%s\n' "$1" >> "$ENV.tmp"; mv "$ENV.tmp" "$ENV"; }
for flag in True '"true"' TRUE ' true '; do
  managed_fixture; set_managed_flag "$flag"; rm "$CLIENT/broker-ca.pem"
  managed_refuses "broker CA"
  echo "PASS managed flag [$flag] is read as enabled"
done
for flag in false False ''; do
  managed_fixture; rm -rf "$CLIENT"; set_managed_flag "$flag"
  PATH="$DIR/nosqlite:$PATH" bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; exit 1; }
  if grep -q 'managed runtime preflight' "$DIR/output"; then exit 1; fi
  echo "PASS managed flag [$flag] is read as off"
done
for flag in yes 1 enabled; do
  managed_fixture; set_managed_flag "$flag"
  managed_refuses 'FLEET_STOCK_MANAGED_ENABLED must be true or false'
done
# setup.sh shares the helper and runs it before the rollout guard and any orchestrator replacement.
pre_line=$(grep -n '^stock_managed_preflight ' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
[[ -n "$pre_line" && "$pre_line" -lt "$guard_line" ]]
grep -q '^source "$SCRIPT_DIR/scripts/lib/stock-managed-preflight.sh"$' "$ROOT/setup.sh"
echo 'PASS setup.sh runs the shared managed preflight before the rollout guard'
# #492 review F9r: setup.sh also runs the backup freshness check, after the shared preflight and
# before the rollout guard, exactly like upgrade.
fresh_line=$(grep -n '^stock_backup_freshness "\$ENV_FILE" "\$SCRIPT_DIR"' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
[[ -n "$fresh_line" && "$pre_line" -lt "$fresh_line" && "$fresh_line" -lt "$guard_line" ]]
echo 'PASS setup.sh runs the gateway backup freshness check before the rollout guard'
# #492 review F9r: run setup.sh's own guard block. --skip-build inspects the local fleet:orchestrator
# image it will start; a build checks the Dockerfile. Stubbed docker, no network.
block=$(awk '/^stock_backup_freshness /{on=1} on{print} on&&/^fi$/{n++; if(n==2) exit}' "$ROOT/setup.sh")
[[ "$block" == *'--image fleet:orchestrator'* ]]
GUARDBIN="$DIR/guardbin"; mkdir -p "$GUARDBIN"
cat > "$GUARDBIN/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CALLS"
[[ "$*" == 'image inspect fleet:orchestrator' && -n "${NO_LOCAL_IMAGE:-}" ]] && exit 1
[[ "$*" == 'image inspect --format'*' fleet:orchestrator' ]] && printf '1'
[[ "$*" == 'run --rm --entrypoint openshell '* ]] && printf '%s\n' "${STUB_CLI_VERSION-openshell 0.1.5}"
exit 0
MOCK
chmod +x "$GUARDBIN/docker"
managed_fixture; set_managed_flag false
run_setup_guard() {
  : > "$CALLS"
  PATH="$GUARDBIN:$PATH" SKIP_BUILD="$1" DRY_RUN=false SCRIPT_DIR="$DIR" ENV_FILE="$ENV" BLOCK="$block" bash -c '
    set -euo pipefail
    source "$SCRIPT_DIR/scripts/lib/stock-managed-preflight.sh"
    read_env_var() { echo fixture-token; }
    stock_backup_freshness() { echo "freshness $*" >> "$CALLS"; }
    fail() { echo "FAIL: $*" >&2; }
    eval "$BLOCK"' > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; return 1; }
}
run_setup_guard true
grep -q '^freshness ' "$CALLS"
grep -q '^image inspect --format .* fleet:orchestrator$' "$CALLS"
grep -q 'OK: target supports managed bindings' "$DIR/output"
echo 'PASS setup.sh --skip-build guards the fleet:orchestrator image it starts'
run_setup_guard false
grep -q '^freshness ' "$CALLS"
if grep -q '^image inspect' "$CALLS"; then exit 1; fi
grep -q 'OK: target supports managed bindings' "$DIR/output"
echo 'PASS setup.sh build run guards the orchestrator Dockerfile'
if grep -q 'entrypoint openshell' "$CALLS"; then exit 1; fi
echo 'PASS default-off setup guard never checks the stock CLI'
# #492 A8: --skip-build without a local fleet:orchestrator image refuses before the rollout guard.
if NO_LOCAL_IMAGE=1 run_setup_guard true 2>/dev/null; then exit 1; fi
grep -q -- '--skip-build needs an existing local fleet:orchestrator image' "$DIR/output"
if grep -q '^image inspect --format' "$CALLS"; then exit 1; fi
echo 'PASS setup.sh --skip-build without a local image refuses before replacing anything'
# #492 A3: a managed --skip-build starts the existing image, so it must carry the pinned stock CLI.
managed_fixture
run_setup_guard true
grep -qx 'run --rm --entrypoint openshell fleet:orchestrator --version' "$CALLS"
echo 'PASS managed setup.sh --skip-build accepts a local image with the pinned stock CLI'
if STUB_CLI_VERSION= run_setup_guard true 2>/dev/null; then exit 1; fi
grep -q 'lacks the stock CLI openshell 0.1.5' "$DIR/output"
if grep -q '^image inspect --format' "$CALLS"; then exit 1; fi
echo 'PASS managed setup.sh --skip-build refuses a local image without the pinned stock CLI'
# setup.sh passes the same build args to its orchestrator build and checks the result.
grep -q 'build_image "fleet:orchestrator" .*"$ORCH_CLI_ARGS"$' "$ROOT/setup.sh"
grep -q 'stock_cli_image_check "$ENV_FILE" fleet:orchestrator' "$ROOT/setup.sh"
echo 'PASS setup.sh builds the orchestrator with the pinned stock CLI args and checks the image'
