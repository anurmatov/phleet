#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
source "$ROOT/scripts/lib/comms-profiles.sh"
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
FILE="$DIR/.env"
fixture() { printf '%s' "$1" > "$DIR/fixture"; cp "$DIR/fixture" "$FILE"; }
profiles() {
  fixture "$2"
  [[ "$(comms_resolve_profiles "$FILE")" == "$3" ]]
  echo "PASS $1"
}
profiles disabled $'FLEET_COMMS_ENABLED=false\nFLEET_COMMS_MEDIA_ENDPOINT=http://store\nFLEET_COMMS_MEDIA_STORE=invalid\n' ''
profiles absent '' ''
profiles text $'FLEET_COMMS_ENABLED=true\nFLEET_COMMS_MEDIA_STORE=invalid\n' '--profile comms'
profiles seaweedfs $'FLEET_COMMS_ENABLED=true\nFLEET_COMMS_MEDIA_ENDPOINT=http://store\nFLEET_COMMS_MEDIA_STORE=seaweedfs\n' '--profile comms --profile comms-media-seaweedfs'
profiles minio $'FLEET_COMMS_ENABLED=true\nFLEET_COMMS_MEDIA_ENDPOINT=http://store\nFLEET_COMMS_MEDIA_STORE=minio\n' '--profile comms --profile comms-media'
fixture $'FLEET_COMMS_ENABLED=true\nFLEET_COMMS_MEDIA_ENDPOINT=http://store\nFLEET_COMMS_MEDIA_STORE=bad\n'
code=0; output=$(comms_resolve_profiles "$FILE" 2>&1) || code=$?
[[ $code == 2 && "$output" == 'invalid FLEET_COMMS_MEDIA_STORE=bad; expected seaweedfs or minio' ]]
echo 'PASS invalid store'
for input in 'FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000' 'FLEET_COMMS_MINIO_ROOT_USER=legacy-user'; do
  fixture "$input" # Deliberately no final newline.
  [[ "$(comms_backfill_store_key "$FILE")" == 'recorded FLEET_COMMS_MEDIA_STORE=minio' ]]
  [[ "$(grep '^FLEET_COMMS_MEDIA_STORE=' "$FILE")" == 'FLEET_COMMS_MEDIA_STORE=minio' ]]
  grep -qx "$input" "$FILE"
  cp "$FILE" "$DIR/before"
  [[ -z "$(comms_backfill_store_key "$FILE")" ]]
  cmp "$FILE" "$DIR/before"
  echo "PASS backfill $input and idempotence"
done
fixture $'FLEET_COMMS_MEDIA_STORE=minio\nFLEET_COMMS_MEDIA_ENDPOINT=\nFLEET_COMMS_MEDIA_BUCKET=custom-b\nFLEET_COMMS_MEDIA_SECRET_KEY=keep-me\n'
comms_enable_media "$FILE"
[[ "$(grep '^FLEET_COMMS_MEDIA_' "$FILE")" == $'FLEET_COMMS_MEDIA_STORE=minio\nFLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000\nFLEET_COMMS_MEDIA_BUCKET=custom-b\nFLEET_COMMS_MEDIA_SECRET_KEY=keep-me' ]]
echo 'PASS legacy re-enable preserves bucket and credential'
fixture ''
comms_enable_media "$FILE"
[[ "$(cat "$FILE")" == $'FLEET_COMMS_MEDIA_STORE=seaweedfs\nFLEET_COMMS_MEDIA_ENDPOINT=http://comms-seaweedfs:8333\nFLEET_COMMS_MEDIA_BUCKET=comms-journal' ]]
cp "$FILE" "$DIR/before"; comms_enable_media "$FILE"; cmp "$FILE" "$DIR/before"
echo 'PASS fresh media and idempotence'
fixture $'FLEET_COMMS_MEDIA_STORE=seaweedfs\nFLEET_COMMS_MEDIA_BUCKET=custom-b\n'
comms_enable_media "$FILE"
grep -qx 'FLEET_COMMS_MEDIA_ENDPOINT=http://comms-seaweedfs:8333' "$FILE"
grep -qx 'FLEET_COMMS_MEDIA_BUCKET=custom-b' "$FILE"
echo 'PASS seaweedfs re-enable'
cp "$FILE" "$DIR/before"
COMMS_DRY_RUN=true comms_env_set "$FILE" FLEET_COMMS_MEDIA_STORE minio >/dev/null
cmp "$FILE" "$DIR/before"
echo 'PASS dry-run preserves file'
# /proc is unwritable even when the suite runs as root.
if comms_env_set /proc/version FLEET_COMMS_MEDIA_STORE minio 2>/dev/null; then exit 1; fi
echo 'PASS unwritable file'
