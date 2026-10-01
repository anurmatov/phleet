#!/bin/sh
set -eu
bucket=${FLEET_COMMS_MEDIA_BUCKET:-}
case "$bucket" in
  ''|*[!a-z0-9-]*|-*|*-) printf '%s\n' 'init-bucket.sh: FLEET_COMMS_MEDIA_BUCKET invalid bucket' >&2; exit 1 ;;
esac
if [ "${#bucket}" -lt 3 ] || [ "${#bucket}" -gt 63 ]; then
  printf '%s\n' 'init-bucket.sh: FLEET_COMMS_MEDIA_BUCKET length must be 3..63' >&2; exit 1
fi
# No keys here. The shared network namespace reaches only loopback master/filer, and the
# filesystem is NOT shared with the server. The shell propagates piped command errors.
listing=$(printf 's3.bucket.list\n' | weed shell -master=127.0.0.1:9333 -filer=127.0.0.1:8888)
case "$listing" in
  *"  $bucket$(printf '\t')"*) exit 0 ;;
esac
printf 's3.bucket.create -name=%s\n' "$bucket" | weed shell -master=127.0.0.1:9333 -filer=127.0.0.1:8888
