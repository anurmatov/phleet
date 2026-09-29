#!/bin/sh
# Provision the journal object store, on first start only.
#
# ONE SHOT, run by the comms-minio-init container. It is the ONLY place the root credential is
# used after the server starts, and the only place the bucket's anonymous policy is ever written.
#
# ⚠️ THE SCOPED USER IS THE POINT. fleet-comms and fleet-comms-ops run as
# `comms_media_runtime`, which holds GetObject, PutObject, DeleteObject, ListBucket and
# AbortMultipartUpload on this one bucket. Root additionally holds the admin actions that change
# the bucket's OWN policy — including `mc anonymous set public`.
#
# Comms refuses to boot when an unsigned ListObjectsV2 on this bucket answers anything other than
# 403 (see docs/comms-journal.md). That check is only a guard if nothing in the running fleet can
# produce the configuration it checks for. Give a long-running service the root key and the guard
# becomes a restart away from being switched off by the very process it protects; give it the
# scoped key and the state the guard detects is one the deployment cannot reach by accident.
#
# The same argument, one service over, is why comms-mysql hands its runtime account no DDL grant.
set -eu

: "${MINIO_ROOT_USER:?set FLEET_COMMS_MINIO_ROOT_USER on the comms-minio-init service}"
: "${MINIO_ROOT_PASSWORD:?set FLEET_COMMS_MINIO_ROOT_PASSWORD on the comms-minio-init service}"
: "${FLEET_COMMS_MEDIA_BUCKET:?set FLEET_COMMS_MEDIA_BUCKET}"
: "${FLEET_COMMS_MEDIA_ACCESS_KEY:?set FLEET_COMMS_MEDIA_ACCESS_KEY}"
: "${FLEET_COMMS_MEDIA_SECRET_KEY:?set FLEET_COMMS_MEDIA_SECRET_KEY}"

# The credential is passed to `mc alias set` through the environment rather than the command line:
# an argument is visible in `ps` to anything else in the container for as long as it runs.
export MC_ACCESS_KEY="$MINIO_ROOT_USER"
export MC_SECRET_KEY="$MINIO_ROOT_PASSWORD"

mc alias set comms http://comms-minio:9000

# The bucket. Private by default, and this script never touches its policy again — there is no
# `mc anonymous` line here to delete later, which is the difference between a guard and a habit.
mc mb --ignore-existing "comms/${FLEET_COMMS_MEDIA_BUCKET}"

# The scoped runtime user. Idempotent: `mc admin user add` on an existing access key updates it, so
# rotating FLEET_COMMS_MEDIA_SECRET_KEY and recreating this container is the rotation path.
mc admin user add comms "$FLEET_COMMS_MEDIA_ACCESS_KEY" "$FLEET_COMMS_MEDIA_SECRET_KEY"

# The policy is generated, not copied. MinIO interpolates `${aws:username}` inside a policy
# document; the bucket name here is a plain variable this script substitutes with sed. A literal
# bucket name in the tracked JSON would be a policy every deployment that copied this file and
# renamed its bucket silently grants over NOTHING — the scoped user would hold access to a bucket
# that does not exist, and to no bucket that does.
sed "s|\${comms-journal}|${FLEET_COMMS_MEDIA_BUCKET}|g" \
  /init/comms-runtime-policy.json > /tmp/comms-runtime-policy.json

mc admin policy create comms comms-journal-runtime /tmp/comms-runtime-policy.json \
  || mc admin policy update comms comms-journal-runtime /tmp/comms-runtime-policy.json

mc admin policy attach comms comms-journal-runtime --user "$FLEET_COMMS_MEDIA_ACCESS_KEY"

# The check that would have caught a wrong policy at deploy rather than at the first upload: the
# scoped key can put, and the scoped key CANNOT read the bucket's anonymous policy.
mc alias set runtime http://comms-minio:9000 "$FLEET_COMMS_MEDIA_ACCESS_KEY" "$FLEET_COMMS_MEDIA_SECRET_KEY"
mc ls "runtime/${FLEET_COMMS_MEDIA_BUCKET}" >/dev/null

echo "comms-minio-init: bucket ${FLEET_COMMS_MEDIA_BUCKET} ready, runtime policy attached"
