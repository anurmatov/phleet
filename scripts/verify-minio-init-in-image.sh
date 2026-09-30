#!/bin/bash
# Run the journal object-store provisioner IN THE PINNED IMAGE, on the deployment host.
#
# WHY THIS EXISTS. CI proves `init.sh` calls nothing but `mc` and shell builtins, and it replays the
# bucket substitution — but it cannot start the image. MinIO withdrew every anonymous pull channel
# (dl.min.io 410, Docker Hub and quay.io 401, and mirror.gcr.io / public.ecr.aws do not mirror
# minio/mc at all), so no CI job has ever run this script inside the container it ships in. That
# leaves the container-only facts unproven: the compose `entrypoint`, the `:/init:ro` mount, and
# whether the image really has a bash for that shebang to land in.
#
# ⚠️ IT RUNS ITS OWN DISPOSABLE NETWORK AND ITS OWN MinIO SERVER. It never joins shared infrastructure
# and never names the live bucket, so the live runtime policy cannot be a target of anything here.
#
# That isolation is not a nicety — the first version of this script was actively dangerous, and
# `MinioVerifyHelperInvocationTests` now fails the build on each of these:
#
#   1. It ran against the live server. `init.sh` hardcodes the policy name `comms-journal-runtime`,
#      so the verifier created that policy over the live one and then REMOVED it during cleanup —
#      the scoped runtime user loses its policy and every call answers `Access Denied`.
#   2. `docker run … "$IMAGE" --entrypoint /bin/bash -c …` looks right and is not. Everything after
#      the image name is argv for the container's ENTRYPOINT, and this image's ENTRYPOINT is `["mc"]`,
#      so the flags were handed to `mc` and the invocation verified nothing. Every docker flag goes
#      BEFORE the image.
#   3. Cleanup was `|| true` into `/dev/null` and then printed PASS unconditionally, so a failed
#      teardown looked like a successful verification.
#
# Usage (from the fleet checkout on the deploy host):
#   FLEET_COMMS_VERIFY_SERVER_IMAGE=<a pullable MinIO server image> \
#     scripts/verify-minio-init-in-image.sh
#
# The root credentials for the DISPOSABLE server are generated here; nothing is read from the live
# deployment. A server image must be supplied because `minio/mc` ships the client only.
set -eu

IMAGE=minio/mc:RELEASE.2025-08-13T08-35-41Z
REPO=$(cd "$(dirname "$0")/.." && pwd)

SERVER_IMAGE=${FLEET_COMMS_VERIFY_SERVER_IMAGE:-}
if [ -z "$SERVER_IMAGE" ]; then
  echo "FAIL: set FLEET_COMMS_VERIFY_SERVER_IMAGE to a MinIO server image available on this host." >&2
  echo "      minio/mc ships the client only and cannot serve the S3 endpoint init.sh talks to." >&2
  exit 1
fi

# ── disposable everything ────────────────────────────────────────────────────────────────
RUNID=$(date +%s)
NET=zz-minio-verify-$RUNID
SERVER=zz-minio-server-$RUNID
BUCKET=zz-verify-$RUNID
AK=zzverify$RUNID
SK=zz-verify-secret-$RUNID
ROOT_USER=zzverifyroot
ROOT_PASS=zz-verify-root-$RUNID
WORKDIR=$(mktemp -d)
MC_CONFIG="$WORKDIR/mc-config"
mkdir -p "$MC_CONFIG"

CLEANED=0
cleanup() {
  [ "$CLEANED" -eq 1 ] && return 0
  CLEANED=1
  echo "=== cleanup (disposable stack) ==="
  docker rm -f "$SERVER" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  rm -rf "$WORKDIR"
  echo "cleanup: removed server $SERVER and network $NET"
}
# Every exit path tears the disposable stack down, including a failure mid-verification.
trap cleanup EXIT

# `--entrypoint` and every other flag BEFORE the image; only the container command follows it.
run_in_image() { docker run --rm --platform linux/amd64 "$@"; }

echo "=== image identity ==="
docker image inspect "$IMAGE" --format 'image={{.Id}}
created={{.Created}}' 2>/dev/null || docker pull --platform linux/amd64 "$IMAGE"
run_in_image --entrypoint /bin/bash "$IMAGE" -c \
  'echo "mc=$(/usr/bin/mc --version | head -1)"; echo "bash=$(/bin/bash --version | head -1)";
   for t in sed awk grep envsubst; do command -v "$t" >/dev/null 2>&1 && echo "$t=PRESENT" || echo "$t=ABSENT"; done'

echo "=== disposable network + MinIO server ==="
docker network create --driver bridge "$NET" >/dev/null
docker run -d --name "$SERVER" --platform linux/amd64 --network "$NET" \
  -e MINIO_ROOT_USER="$ROOT_USER" -e MINIO_ROOT_PASSWORD="$ROOT_PASS" \
  "$SERVER_IMAGE" server /data >/dev/null

# init.sh resolves its endpoint from the compose service name. Rather than edit the script, that name
# is aliased INSIDE the container to the disposable server's address, so the file that runs is
# byte-for-byte the shipped one.
COMMS_HOST=comms-minio
SERVER_IP=""
for _ in $(seq 1 30); do
  SERVER_IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$SERVER" 2>/dev/null || true)
  [ -n "$SERVER_IP" ] && break
  sleep 2
done
if [ -z "$SERVER_IP" ]; then
  echo "FAIL: could not resolve the disposable server's address" >&2
  exit 1
fi
echo "server $SERVER is at $SERVER_IP on network $NET"

# Readiness: the server must accept an alias before init.sh is pointed at it. Credentials go to
# `mc alias set` on STDIN, never as argv — the same rule init.sh follows.
ready=0
for _ in $(seq 1 30); do
  if printf '%s\n%s\n' "$ROOT_USER" "$ROOT_PASS" |
     run_in_image --network "$NET" --add-host "$COMMS_HOST:$SERVER_IP" \
       --entrypoint /usr/bin/mc "$IMAGE" alias set probe "http://$COMMS_HOST:9000" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 2
done
if [ "$ready" -ne 1 ]; then
  echo "FAIL: the disposable MinIO server never became ready" >&2
  exit 1
fi

echo "=== running the repo's init.sh, unmodified, inside the pinned image ==="
INITDIR="$WORKDIR/init"
mkdir -p "$INITDIR"
cp "$REPO/deploy/comms-minio-init/init.sh" "$REPO/deploy/comms-minio-init/comms-runtime-policy.json" "$INITDIR/"
chmod -R a-w "$INITDIR"
echo "--- shipped files, copied unchanged (sha256): ---"
( cd "$INITDIR" && sha256sum init.sh comms-runtime-policy.json )

set +e
run_in_image --network "$NET" \
  --add-host "$COMMS_HOST:$SERVER_IP" \
  --entrypoint /bin/bash \
  -v "$INITDIR:/init:ro" \
  -v "$MC_CONFIG:/mc" \
  -e MC_CONFIG_DIR=/mc \
  -e MINIO_ROOT_USER="$ROOT_USER" \
  -e MINIO_ROOT_PASSWORD="$ROOT_PASS" \
  -e FLEET_COMMS_MEDIA_BUCKET="$BUCKET" \
  -e FLEET_COMMS_MEDIA_ACCESS_KEY="$AK" \
  -e FLEET_COMMS_MEDIA_SECRET_KEY="$SK" \
  "$IMAGE" /init/init.sh
INIT_EXIT=$?
set -e
echo "=== init.sh EXIT=$INIT_EXIT ==="
if [ "$INIT_EXIT" -ne 0 ]; then
  echo "FAIL: init.sh exited $INIT_EXIT inside $IMAGE" >&2
  exit 1
fi

echo "=== scoped runtime mc ls (proves the policy is ATTACHED, not merely created) ==="
# Only the scoped credentials exist in this container. If attachment silently failed, this goes red.
set +e
printf '%s\n%s\n' "$AK" "$SK" |
  run_in_image --network "$NET" --add-host "$COMMS_HOST:$SERVER_IP" \
    -v "$MC_CONFIG:/mc" -e MC_CONFIG_DIR=/mc \
    --entrypoint /bin/bash "$IMAGE" -c \
    'mc alias set runtime "http://'"$COMMS_HOST"':9000" >/dev/null && mc ls "runtime/'"$BUCKET"'"'
LS_EXIT=$?
set -e
echo "=== runtime mc ls EXIT=$LS_EXIT ==="
if [ "$LS_EXIT" -ne 0 ]; then
  echo "FAIL: scoped runtime mc ls did not succeed (exit $LS_EXIT)" >&2
  exit 1
fi

echo
echo "PASS: init.sh exit 0 inside $IMAGE, scoped runtime mc ls succeeded."
echo "Paste the blocks above into the PR — that is the evidence the review asked for."
