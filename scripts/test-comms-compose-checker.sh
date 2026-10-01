#!/usr/bin/env bash
# Real Compose config only: no daemon or repository configuration writes.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/bin" "$DIR/repo/scripts" "$DIR/scratch"
cp "$ROOT/scripts/check-comms-compose.sh" "$DIR/repo/scripts/"
cp "$ROOT/docker-compose.example.yml" "$ROOT/.env.example" "$DIR/repo/"
REAL_DOCKER=$(command -v docker)
# Embed paths because the checker deliberately clears the process environment.
{
  printf '#!/usr/bin/env bash\nset -euo pipefail\n'
  printf 'REAL_DOCKER=%q\nCALLS=%q\n' "$REAL_DOCKER" "$DIR/calls"
  cat <<'WRAPPER'
[[ "$1" == compose ]]
[[ " $* " == *' config '* ]]
args=()
for arg in "$@"; do
  case "$arg" in --services|--images) ;; *) args+=("$arg") ;; esac
done
# Full config resolves service env_file even on Compose versions whose listing
# shortcuts skip that validation. All results still come from real Compose.
"$REAL_DOCKER" "${args[@]}" > /dev/null
printf '%s\n' "$*" >> "$CALLS"
exec "$REAL_DOCKER" "$@"
WRAPPER
} > "$DIR/bin/docker"
chmod +x "$DIR/bin/docker"
export PATH="$DIR/bin:$PATH"
export TMPDIR="$DIR/scratch"
export COMPOSE_PROFILES=comms,comms-media,comms-media-seaweedfs

bash "$DIR/repo/scripts/check-comms-compose.sh" > "$DIR/output"
[[ "$(grep -c '^PASS ' "$DIR/output")" == 3 ]]
[[ "$(wc -l < "$DIR/calls")" == 4 ]]
[[ ! -e "$DIR/repo/.env" ]]
cmp "$ROOT/.env.example" "$DIR/repo/.env.example"
cmp "$ROOT/docker-compose.example.yml" "$DIR/repo/docker-compose.example.yml"
[[ -z "$(ls -A "$DIR/scratch")" ]]
echo 'PASS fresh checker uses real Compose without repository .env or configuration writes'

# An existing operator configuration must also remain byte-identical.
printf 'OPERATOR_SENTINEL=do-not-change\n' > "$DIR/repo/.env"
cp "$DIR/repo/.env" "$DIR/sentinel"
bash "$DIR/repo/scripts/check-comms-compose.sh" > "$DIR/output"
cmp "$DIR/sentinel" "$DIR/repo/.env"
[[ -z "$(ls -A "$DIR/scratch")" ]]
echo 'PASS existing configuration preserved and disposable checker fixtures removed'
