#!/usr/bin/env bash
# Script-level regression fixtures. These are NOT cold-host or object-preservation evidence.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/scripts/lib" "$DIR/fleet" "$DIR/bin"
cp "$ROOT/setup.sh" "$ROOT/upgrade.sh" "$DIR/"
cp "$ROOT/scripts/lib/comms-profiles.sh" "$DIR/scripts/lib/"
cp "$ROOT/docker-compose.example.yml" "$DIR/"
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
# The epic grant keys (#436) are present, so upgrade has nothing to add and must not touch .env.
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=false
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000
FLEET_COMMS_MEDIA_STORE=minio
FLEET_COMMS_MEDIA_SECRET_KEY=preserve-secret
FLEET_EPIC_GRANTS_ENABLED=false
FLEET_EPIC_GRANTS_MAX_DAYS=14
FLEET_EPIC_GRANTS_DENIED_REPOS=
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
# #436: upgrade adds a missing epic grant key with its default, keeps a present value, and never
# glues a key onto a last line that has no newline.
printf 'FLEET_COMMS_ENABLED=false\nFLEET_EPIC_GRANTS_MAX_DAYS=7' > "$ENV"
bash "$DIR/upgrade.sh" --skip-restart > "$DIR/output" 2>&1
[[ "$(cat "$ENV")" == "$(printf 'FLEET_COMMS_ENABLED=false\nFLEET_EPIC_GRANTS_MAX_DAYS=7\nFLEET_EPIC_GRANTS_ENABLED=false\nFLEET_EPIC_GRANTS_DENIED_REPOS=')" ]]
echo 'PASS upgrade adds missing epic grant keys with defaults and keeps present values'
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
cp "$ROOT/scripts/lib/comms-profiles.sh" "$FRESH/scripts/lib/"
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
