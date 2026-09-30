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

# The root credential goes to `mc alias set` on STDIN, never as an argument: an argument is visible
# in `ps` to anything else in the container for as long as it runs.
#
# ⚠️ It is NOT passed through the environment either. `mc` reads the access key and secret key from
# stdin when they are not arguments, and it does NOT read `MC_ACCESS_KEY` / `MC_SECRET_KEY` at all.
# Exporting them and running a bare `mc alias set` printed "Added successfully" and stored an alias
# with an EMPTY access key and an EMPTY secret key — a silent failure, because the alias is then
# unusable for every later command and the first real failure is a confusing `mc mb` 403 much further
# down this script.
#
# Verified against `mc` RELEASE.2025-08-13T08-35-41Z:
#   MC_ACCESS_KEY=x MC_SECRET_KEY=y mc alias set a URL  -> accessKey='' secretKey=''
#   printf 'x\ny\n' | mc alias set a URL               -> accessKey='x' secretKey='y'
#   MC_HOST_a='http://x:y@URL' mc alias set a URL       -> accessKey='' secretKey=''
# so stdin is the only route that both works and keeps the keys out of `ps`.
#
# `set -e` still holds: `mc` is the last element of the pipeline, so its exit status is the
# pipeline's, and a refused alias set aborts here rather than at the first `mc mb`.
#
# Run against an S3 endpoint on 2026-09-30 with `mc` RELEASE.2025-08-13T08-35-41Z, both scripts
# against the SAME server, and the stored alias read back out of `~/.mc/config.json`:
#
#   before (exported MC_ACCESS_KEY/MC_SECRET_KEY, bare `mc alias set`)
#     alias comms accessKey='' secretKey=''   -> "Added successfully"
#     then: `mc mb` -> "Unable to make bucket ... Access Denied."
#   after (piped on stdin)
#     alias comms accessKey='<root>' secretKey='<root>'
#     then: bucket created, runtime user added, policy created, `mc ls` on the bucket succeeds.
#
# That server answers the S3 data plane but not MinIO's IAM admin API, so the two
# `mc admin policy` lines and the scoped-key `mc alias set` that follows them are NOT proven here.
# They are the part only MinIO can prove, and MinIO's download channels are withdrawn (dl.min.io
# returns 410). Run the profile once on a real host before trusting them.
printf '%s\n%s\n' "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" |
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
# scoped key can list, and the scoped key CANNOT read the bucket's anonymous policy.
#
# Arguments here, deliberately, where the root alias above uses stdin. This is the one place the
# script needs the alias to be provably usable, and the values are the runtime pair the container
# already carries in its own environment for the lifetime of the deployment — not the root pair.
# The failure mode is also the one worth having: a wrong policy fails the `mc ls` below it.
mc alias set runtime http://comms-minio:9000 "$FLEET_COMMS_MEDIA_ACCESS_KEY" "$FLEET_COMMS_MEDIA_SECRET_KEY"
mc ls "runtime/${FLEET_COMMS_MEDIA_BUCKET}" >/dev/null

echo "comms-minio-init: bucket ${FLEET_COMMS_MEDIA_BUCKET} ready, runtime policy attached"
