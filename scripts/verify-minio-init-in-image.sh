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
# The id has to be unique PER RUN, not per second. Two runs started in the same second would
# otherwise share a network, server and access key, and each one's cleanup would tear down the
# other's live resources — the exact cross-run damage this script is supposed to make impossible.
RUNID=$(date +%s)-$$-${RANDOM}
NET=zz-minio-verify-$RUNID
SERVER=zz-minio-server-$RUNID
BIND=zz-minio-verify-bind-$RUNID
BUCKET=zz-verify-$RUNID
AK=zzverify$RUNID
SK=zz-verify-secret-$RUNID
ROOT_USER=zzverifyroot
ROOT_PASS=zz-verify-root-$RUNID
WORKDIR=$(mktemp -d)
MC_CONFIG="$WORKDIR/mc-config"
# The scoped probe gets its OWN config directory. Sharing $MC_CONFIG with init.sh would hand the
# probe the root `comms` alias that init.sh just wrote there, so a probe that is supposed to hold
# ONLY the scoped key would silently be able to fall back to root — and the test would pass while
# proving nothing about the runtime credential.
RUNTIME_CONFIG="$WORKDIR/runtime-config"
mkdir -p "$MC_CONFIG" "$RUNTIME_CONFIG"

# What this run actually created. Cleanup only ever touches these, and only if the create succeeded
# — a run that failed before `docker network create` must not go looking for a network to delete.
CREATED_SERVER=0
CREATED_NETWORK=0
CREATED_BIND=0

# ⚠️ `-i` IS NOT COSMETIC. Two invocations pipe credentials from the host into the container
# (`printf … | run_in_image … alias set …`). Without `-i`/`--interactive` Docker never attaches the
# piped stdin, `mc alias set` reads EOF and stores an alias with an EMPTY access key and secret key —
# and it still prints "Added successfully", because it does not validate at set time. The failure only
# surfaces later as `Access Denied` on the scoped `mc ls`, which looks like a broken policy rather than
# a broken harness. Measured on the deploy host: without `-i` the run fails reproducibly; with `-i`,
# init.sh exits 0 and the scoped listing succeeds.
#
# `--entrypoint` and every other flag still go BEFORE the image; only the container command follows it.
#
# It is defined BEFORE the trap on purpose: the cleanup trap calls it, and a trap firing before this
# definition would die on an undefined function and leak the disposable stack it was meant to remove.
run_in_image() { docker run --rm -i --platform linux/amd64 "$@"; }

CLEANED=0
CLEANUP_FAILED=0
cleanup() {
  # ⚠️ Nothing in here may be an unchecked failing command. Under `set -eu` a failing command inside
  # an EXIT trap aborts the trap: the remaining steps never run and the script exits 1 even when the
  # verification passed. That is what happened here — the container writes root-owned files into the
  # bind-mounted $WORKDIR (no UID remap), `rm -rf` returns Permission denied, and the trap died before
  # removing the network. Measured directly: a bare failing command in an EXIT trap under `set -eu`
  # stops the trap and forces exit 1; the same command with `|| true` does not.
  #
  # So: every step is guarded, removal is then VERIFIED, and a verification failure is recorded rather
  # than swallowed — `PASS` is only printed when cleanup actually confirmed the teardown.
  [ "$CLEANED" -eq 1 ] && return 0
  CLEANED=1
  echo "=== cleanup (disposable stack) ==="

  if [ "$CREATED_SERVER" -eq 1 ]; then
    docker rm -f "$SERVER" >/dev/null 2>&1 || true
    if docker inspect "$SERVER" >/dev/null 2>&1; then
      echo "CLEANUP FAIL: server $SERVER still exists" >&2
      CLEANUP_FAILED=1
    fi
  fi

  if [ "$CREATED_NETWORK" -eq 1 ]; then
    docker network rm "$NET" >/dev/null 2>&1 || true
    if docker network inspect "$NET" >/dev/null 2>&1; then
      echo "CLEANUP FAIL: network $NET still exists" >&2
      CLEANUP_FAILED=1
    fi
  fi

  # Root-owned files from the container are removed BY A CONTAINER, which has the uid to unlink them.
  # The bind is made here rather than at each run: the mount is named, so an interrupted run cannot
  # leak a mount, and the removal target is always this run's own directory — never a path supplied
  # from outside.
  if [ -d "$WORKDIR" ]; then
    if docker volume create "$BIND" >/dev/null 2>&1; then
      CREATED_BIND=1
    fi
    # -v goes BEFORE the image. After the image it is argv for the entrypoint, so the mount silently
    # does not happen and the removal "succeeds" against an empty /work.
    run_in_image -v "$BIND:$WORKDIR" --entrypoint /bin/sh "$IMAGE" \
      -c "rm -rf -- '$WORKDIR'" >/dev/null 2>&1 || true
    # The host-side user still owns the files it made itself, so try that too.
    rm -rf "$WORKDIR" 2>/dev/null || true
    if [ -d "$WORKDIR" ]; then
      echo "CLEANUP FAIL: $WORKDIR still exists — a root-owned temp dir was left on this host" >&2
      CLEANUP_FAILED=1
    fi
  fi

  if [ "$CREATED_BIND" -eq 1 ]; then
    docker volume rm "$BIND" >/dev/null 2>&1 || true
    if docker volume inspect "$BIND" >/dev/null 2>&1; then
      echo "CLEANUP FAIL: bind volume $BIND still exists" >&2
      CLEANUP_FAILED=1
    fi
  fi

  if [ "$CLEANUP_FAILED" -eq 0 ]; then
    echo "cleanup: server and network removed, temp dir deleted (verified)"
  fi

  # ⚠️ The exit status is set HERE, inside the trap. A trailing `exit 0` in the body cannot express
  # "verification passed but teardown failed", because an EXIT trap runs AFTER that statement — the
  # body would already have committed to 0. Assigning the status in the trap is the only place that
  # knows both results.
  if [ "${VERIFIED:-0}" -eq 1 ] && [ "$CLEANUP_FAILED" -eq 0 ]; then
    echo "PASS: verification succeeded and teardown was confirmed."
    echo "Paste the blocks above into the PR — that is the evidence the review asked for."
    return 0
  fi

  if [ "${VERIFIED:-0}" -eq 1 ]; then
    echo "INCOMPLETE: verification succeeded but cleanup did not confirm teardown." >&2
  fi
  return 1
}
# Every exit path tears the disposable stack down, including a failure mid-verification.
trap cleanup EXIT
VERIFIED=0

echo "=== image identity ==="
docker image inspect "$IMAGE" --format 'image={{.Id}}
created={{.Created}}' 2>/dev/null || docker pull --platform linux/amd64 "$IMAGE"
run_in_image --entrypoint /bin/bash "$IMAGE" -c \
  'echo "mc=$(/usr/bin/mc --version | head -1)"; echo "bash=$(/bin/bash --version | head -1)";
   for t in sed awk grep envsubst; do command -v "$t" >/dev/null 2>&1 && echo "$t=PRESENT" || echo "$t=ABSENT"; done'

echo "=== disposable network + MinIO server ==="
docker network create --driver bridge "$NET" >/dev/null
CREATED_NETWORK=1
run_in_image -d --name "$SERVER" --platform linux/amd64 --network "$NET" \
  -e MINIO_ROOT_USER="$ROOT_USER" -e MINIO_ROOT_PASSWORD="$ROOT_PASS" \
  "$SERVER_IMAGE" server /data >/dev/null
CREATED_SERVER=1

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
# Fresh, empty config dir holding ONLY the scoped alias. If this mounted $MC_CONFIG it would inherit
# the root `comms` alias init.sh wrote there, and the probe would pass on root credentials while
# claiming to prove the scoped one.
printf '%s\n%s\n' "$AK" "$SK" |
  run_in_image --network "$NET" --add-host "$COMMS_HOST:$SERVER_IP" \
    -v "$RUNTIME_CONFIG:/mc" -e MC_CONFIG_DIR=/mc \
    --entrypoint /bin/bash "$IMAGE" -c \
    'mc alias set runtime "http://'"$COMMS_HOST"':9000" >/dev/null && mc ls "runtime/'"$BUCKET"'"'
LS_EXIT=$?
set -e
echo "=== runtime mc ls EXIT=$LS_EXIT ==="
if [ "$LS_EXIT" -ne 0 ]; then
  echo "FAIL: scoped runtime mc ls did not succeed (exit $LS_EXIT)" >&2
  exit 1
fi

# The trap runs after this point, so record the verification result now and print the verdict from
# inside the trap — otherwise PASS is printed before teardown is known, which is what let a leaked
# temp dir coexist with a green line.
echo "VERIFIED: init.sh exit 0 inside $IMAGE, scoped runtime mc ls succeeded."
VERIFIED=1
# The script ends here on purpose. The EXIT trap runs next, and IT decides the exit status — so a
# full pass that leaks a root-owned temp dir exits non-zero rather than printing PASS over a leak.

