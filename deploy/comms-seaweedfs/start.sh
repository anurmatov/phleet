#!/bin/sh
set -eu
bucket=${FLEET_COMMS_MEDIA_BUCKET:-}
access=${FLEET_COMMS_MEDIA_ACCESS_KEY:-}
secret=${FLEET_COMMS_MEDIA_SECRET_KEY:-}
signing=${WEED_JWT_FILER_SIGNING_KEY:-}
# Validate before creating an identity or opening any listener. Restricted alphabets also make
# the JSON below safe without an escaping tool in this image.
case "$bucket" in
  ''|*[!a-z0-9-]*|-*|*-) printf '%s\n' 'start.sh: FLEET_COMMS_MEDIA_BUCKET invalid bucket' >&2; exit 1 ;;
esac
if [ "${#bucket}" -lt 3 ] || [ "${#bucket}" -gt 63 ]; then
  printf '%s\n' 'start.sh: FLEET_COMMS_MEDIA_BUCKET length must be 3..63' >&2; exit 1
fi
case "$access" in
  ''|*[!A-Za-z0-9+/=_-]*) printf '%s\n' 'start.sh: FLEET_COMMS_MEDIA_ACCESS_KEY invalid credential' >&2; exit 1 ;;
esac
if [ "${#access}" -lt 8 ] || [ "${#access}" -gt 128 ]; then
  printf '%s\n' 'start.sh: FLEET_COMMS_MEDIA_ACCESS_KEY length must be 8..128' >&2; exit 1
fi
case "$secret" in
  ''|*[!A-Za-z0-9+/=_-]*) printf '%s\n' 'start.sh: FLEET_COMMS_MEDIA_SECRET_KEY invalid credential' >&2; exit 1 ;;
esac
if [ "${#secret}" -lt 8 ] || [ "${#secret}" -gt 128 ]; then
  printf '%s\n' 'start.sh: FLEET_COMMS_MEDIA_SECRET_KEY length must be 8..128' >&2; exit 1
fi
case "$signing" in
  ''|*[!A-Za-z0-9]*) printf '%s\n' 'start.sh: WEED_JWT_FILER_SIGNING_KEY invalid signing key' >&2; exit 1 ;;
esac
if [ "${#signing}" -lt 32 ] || [ "${#signing}" -gt 128 ]; then
  printf '%s\n' 'start.sh: WEED_JWT_FILER_SIGNING_KEY length must be 32..128' >&2; exit 1
fi
umask 077
identity=/tmp/comms-media-identity.json
printf '{"identities":[{"name":"comms_media_runtime","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Read:%s","Write:%s","List:%s"]}]}\n' "$access" "$secret" "$bucket" "$bucket" "$bucket" > "$identity"
exec weed server -dir=/data -ip=127.0.0.1 -ip.bind=127.0.0.1 -s3 -s3.ip.bind=0.0.0.0 -s3.port=8333 -s3.port.grpc=18333 -s3.port.https=0 -s3.port.iceberg=0 -s3.port.lance=0 -s3.iam=false -s3.iam.config="$identity" -metricsPort=0 -debug=false -sftp=false -webdav=false -iam=false -mq.broker=false -mq.agent=false
