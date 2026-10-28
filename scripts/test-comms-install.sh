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
cp "$ROOT/scripts/check-stock-runtime-rollout.sh" "$DIR/scripts/"
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
# Epic watcher/grant upgrades append missing defaults, keep a present value, and never
# glues a key onto a last line that has no newline.
printf 'FLEET_COMMS_ENABLED=false\nFLEET_EPIC_GRANTS_MAX_DAYS=7' > "$ENV"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1
[[ "$(cat "$ENV")" == "$(printf 'FLEET_COMMS_ENABLED=false\nFLEET_EPIC_GRANTS_MAX_DAYS=7\nFLEET_EPIC_WATCHER_ENABLED=false\nFLEET_EPIC_WATCHER_INTERVAL_SECONDS=300\nFLEET_EPIC_GRANTS_ENABLED=false\nFLEET_EPIC_GRANTS_DENIED_REPOS=\nFLEET_AUTONOMY_ENABLED=false\nFLEET_STOCK_MANAGED_ENABLED=false')" ]]
echo 'PASS upgrade adds missing epic watcher/grant keys with defaults and keeps present values'
sed -i.bak 's/^FLEET_AUTONOMY_ENABLED=false$/FLEET_AUTONOMY_ENABLED=true/' "$ENV"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1
[[ "$(grep -c '^FLEET_AUTONOMY_ENABLED=' "$ENV")" == 1 ]]
grep -qx FLEET_AUTONOMY_ENABLED=true "$ENV"
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
[[ ! -e "$FRESH/fleet" && ! -e "$FRESH/.env" ]]
cmp "$FRESH/.env.example" "$DIR/example-before"
[[ -z "$(ls -A "$DIR/previews")" ]]
! grep -Eq '^build |^compose .* (pull|up|down|rm|run)|^network create ' "$CALLS"
echo 'PASS fresh full dry-run exits zero without real config or leftover previews'
# --skip-build still replaces the orchestrator, so the rollout guard must run before the build branch.
guard_line=$(grep -n 'check-stock-runtime-rollout.sh' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
skip_line=$(grep -n '^if \$SKIP_BUILD; then' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
[[ -n "$guard_line" && -n "$skip_line" && "$guard_line" -lt "$skip_line" ]]
echo 'PASS setup.sh runs the rollout guard regardless of --skip-build'
# #492 F8: an enabled managed runtime refuses incomplete trust before any lifecycle change.
sed -i.bak '/ pull "\*) exit 1 ;;/d' "$DIR/bin/docker"
mkdir -p "$DIR/nosqlite"
printf '#!/usr/bin/env bash\nexit 1\n' > "$DIR/nosqlite/python3"
chmod +x "$DIR/nosqlite/python3"
CLIENT="$DIR/fleet/openshell-client"
MTLS="$CLIENT/openshell/gateways/fixture/mtls"
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
ENV
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
managed_fixture
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; exit 1; }
if grep -q 'managed runtime preflight' "$DIR/output"; then exit 1; fi
echo 'PASS managed upgrade with complete trust passes the preflight'
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
# Default-off: none of that trust exists and python3 lacks sqlite3, yet nothing changes.
managed_fixture; rm -rf "$CLIENT"
sed -i.bak -e 's/^FLEET_STOCK_MANAGED_ENABLED=true$/FLEET_STOCK_MANAGED_ENABLED=false/' -e '/^FLEET_STOCK_BROKER/d' "$ENV"
PATH="$DIR/nosqlite:$PATH" bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1 || { cat "$DIR/output" >&2; exit 1; }
if grep -q 'managed runtime preflight' "$DIR/output"; then exit 1; fi
echo 'PASS default-off upgrade skips the managed preflight'
# setup.sh shares the helper and runs it before the rollout guard and any orchestrator replacement.
pre_line=$(grep -n '^stock_managed_preflight ' "$ROOT/setup.sh" | head -1 | cut -d: -f1)
[[ -n "$pre_line" && "$pre_line" -lt "$guard_line" ]]
grep -q '^source "$SCRIPT_DIR/scripts/lib/stock-managed-preflight.sh"$' "$ROOT/setup.sh"
echo 'PASS setup.sh runs the shared managed preflight before the rollout guard'
