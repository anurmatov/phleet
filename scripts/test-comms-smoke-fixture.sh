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
cat > "$DIR/bin/docker" <<'MOCK'
#!/usr/bin/env bash
case "$*" in
  'container inspect '*|'network inspect '*|'volume inspect '*) exit 1 ;;
  'compose '*' config '*) exec "$REAL_DOCKER" "$@" ;;
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
