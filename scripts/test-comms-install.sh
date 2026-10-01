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
cat > "$ENV" <<'ENV'
FLEET_COMMS_ENABLED=false
FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000
FLEET_COMMS_MEDIA_STORE=minio
FLEET_COMMS_MEDIA_SECRET_KEY=preserve-secret
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
