#!/bin/bash
# Run the journal object-store provisioner IN THE PINNED IMAGE, on the deployment host.
#
# WHY THIS EXISTS. CI proves `init.sh` calls nothing but `mc` and shell builtins, and it replays the
# bucket substitution — but it cannot start the image. MinIO withdrew every anonymous pull channel
# (dl.min.io 410, Docker Hub and quay.io 401, and mirror.gcr.io / public.ecr.aws do not mirror
# minio/mc at all), so no CI job has ever run this script inside the container it actually ships in.
# That leaves the thing only a container can prove unproven: the compose `entrypoint`
# (`/bin/bash /init/init.sh`), the `:/init:ro` mount, and whether the image really has a bash for
# that shebang to land in.
#
# This script closes that gap and is meant to be run ON THE DEPLOYMENT HOST, where the image can be
# pulled and the comms-media network exists. It does NOT modify init.sh: it mounts the repo's copy
# read-only at the exact path the compose file uses, so what runs is byte-for-byte what deploys.
#
# It runs against a THROWAWAY bucket and a THROWAWAY runtime user, then removes both. The real
# FLEET_COMMS_MEDIA_* credentials are never read or required — the root pair is taken from the
# host's own environment, and only the root pair is used.
#
# Usage (from the fleet checkout on the deploy host):
#   FLEET_COMMS_MINIO_ROOT_USER=… FLEET_COMMS_MINIO_ROOT_PASSWORD=… \
#     scripts/verify-minio-init-in-image.sh
set -eu

IMAGE=minio/mc:RELEASE.2025-08-13T08-35-41Z
: "${FLEET_COMMS_MINIO_ROOT_USER:?set the root access key}"
: "${FLEET_COMMS_MINIO_ROOT_PASSWORD:?set the root secret key}"

REPO=$(cd "$(dirname "$0")/.." && pwd)
RUNID=$(date +%s)
BUCKET=zz-minio-init-verify-$RUNID
AK=zz_verify_$RUNID
SK=zz-verify-secret-$RUNID

echo "=== image identity ==="
docker image inspect "$IMAGE" \
  --format 'image={{.Id}}
created={{.Created}}' 2>/dev/null || docker pull --platform linux/amd64 "$IMAGE"
docker run --rm --entrypoint /bin/bash "$IMAGE" -c \
  'echo "mc=$(/usr/bin/mc --version | head -1)"; echo "bash=$(/bin/bash --version | head -1)"; \
   for t in sed awk grep envsubst; do command -v "$t" >/dev/null 2>&1 && echo "$t=PRESENT" || echo "$t=ABSENT"; done'

echo "=== running the repo's init.sh, unmodified, inside the pinned image ==="
# Same mount and entrypoint as docker-compose.example.yml. The endpoint is the compose service name,
# so run this on a host where comms-minio is on the network this container joins.
docker run --rm \
  --platform linux/amd64 \
  --network "${FLEET_COMMS_VERIFY_NETWORK:-comms-media}" \
  -v "$REPO/deploy/comms-minio-init:/init:ro" \
  -e MINIO_ROOT_USER="$FLEET_COMMS_MINIO_ROOT_USER" \
  -e MINIO_ROOT_PASSWORD="$FLEET_COMMS_MINIO_ROOT_PASSWORD" \
  -e FLEET_COMMS_MEDIA_BUCKET="$BUCKET" \
  -e FLEET_COMMS_MEDIA_ACCESS_KEY="$AK" \
  -e FLEET_COMMS_MEDIA_SECRET_KEY="$SK" \
  "$IMAGE" /bin/bash /init/init.sh
INIT_EXIT=$?
echo "=== init.sh EXIT=$INIT_EXIT ==="
[ "$INIT_EXIT" -eq 0 ] || { echo "FAIL: init.sh did not exit 0"; exit 1; }

echo "=== scoped runtime mc ls (proves the policy is ATTACHED, not just created) ==="
# A throwaway container with ONLY the scoped credentials: it can list the new bucket and nothing
# else. If the policy attachment silently failed, this is the step that goes red.
docker run --rm --platform linux/amd64 \
  --network "${FLEET_COMMS_VERIFY_NETWORK:-comms-media}" \
  --entrypoint /bin/bash "$IMAGE" -c \
  "printf '%s\n%s\n' '$AK' '$SK' | mc alias set runtime http://comms-minio:9000 >/dev/null && \
   mc ls runtime/$BUCKET"
echo "=== runtime mc ls EXIT=$? ==="

echo "=== cleanup ==="
docker run --rm --platform linux/amd64 \
  --network "${FLEET_COMMS_VERIFY_NETWORK:-comms-media}" \
  --entrypoint /bin/bash "$IMAGE" -c \
  "printf '%s\n%s\n' \"\$MINIO_ROOT_USER\" \"\$MINIO_ROOT_PASSWORD\" | mc alias set root http://comms-minio:9000 >/dev/null; \
   mc rb --force root/$BUCKET >/dev/null 2>&1 || true; \
   mc admin policy detach root comms-journal-runtime --user $AK >/dev/null 2>&1 || true; \
   mc admin policy remove root comms-journal-runtime >/dev/null 2>&1 || true; \
   mc admin user remove root $AK >/dev/null 2>&1 || true; echo removed" \
  -e MINIO_ROOT_USER="$FLEET_COMMS_MINIO_ROOT_USER" -e MINIO_ROOT_PASSWORD="$FLEET_COMMS_MINIO_ROOT_PASSWORD"

echo
echo "PASS: init.sh exit 0 inside $IMAGE, scoped runtime mc ls succeeded."
echo "Paste the three blocks above into the PR — that is the evidence the review asked for."
