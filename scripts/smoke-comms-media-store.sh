#!/usr/bin/env bash
# Disposable CI only. Exercise the exact example services, never the live stack.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
: "${SEAWEEDFS_IMAGE:?CI must pass the pinned image}"
: "${GRPCURL_IMAGE:?CI must pass the pinned image}"
NETWORK=comms397-smoke_comms-media
for name in comms-seaweedfs comms-seaweedfs-init comms397-invalid; do
  if docker container inspect "$name" >/dev/null 2>&1; then
    echo "Refusing to touch existing container $name" >&2; exit 1
  fi
done
for resource in network volume; do
  name=$NETWORK
  [[ "$resource" != volume ]] || name=comms397-smoke_comms_seaweedfs_data
  if docker "$resource" inspect "$name" >/dev/null 2>&1; then
    echo "Refusing to touch existing $resource $name" >&2; exit 1
  fi
done
FIXTURE=$(mktemp -d)
ENV="$FIXTURE/.env"
# Compose resolves service env_file and relative binds beside its compose file, not --env-file.
# Keep the approved example unchanged and supply both in a disposable project directory.
cp "$ROOT/docker-compose.example.yml" "$FIXTURE/docker-compose.yml"
ln -s "$ROOT/deploy" "$FIXTURE/deploy"
# The example has a fixed network name. Override only the disposable fixture, never share it.
printf 'networks:\n  comms-media:\n    name: %s\n' "$NETWORK" > "$FIXTURE/network.yml"
compose() { docker compose -p comms397-smoke -f "$FIXTURE/docker-compose.yml" -f "$FIXTURE/network.yml" --env-file "$ENV" --profile comms-media-seaweedfs "$@"; }
cleanup() {
  compose down >/dev/null 2>&1 || true
  docker rm -f comms397-invalid >/dev/null 2>&1 || true
  docker volume rm comms397-smoke_comms_seaweedfs_data >/dev/null 2>&1 || true
  rm -rf "$FIXTURE"
}
trap cleanup EXIT
ACCESS=$(openssl rand -hex 16)
SECRET=$(openssl rand -hex 24)
SIGNING=$(openssl rand -hex 32)
printf 'FLEET_BASE_DIR=%s\nFLEET_COMMS_MEDIA_BUCKET=comms-journal\nFLEET_COMMS_MEDIA_ACCESS_KEY=%s\nFLEET_COMMS_MEDIA_SECRET_KEY=%s\nFLEET_COMMS_SEAWEEDFS_SIGNING_KEY=%s\n' "$FIXTURE" "$ACCESS" "$SECRET" "$SIGNING" > "$ENV"
actual=$(compose config --format json | python3 -c 'import json,sys; config=json.load(sys.stdin); assert config["networks"]["comms-media"]["name"] == sys.argv[1]; print(config["services"]["comms-seaweedfs"]["image"])' "$NETWORK")
[[ "$actual" == "$SEAWEEDFS_IMAGE" ]]
docker pull "$SEAWEEDFS_IMAGE"
docker pull "$GRPCURL_IMAGE" # Failure is fatal, never skipped.
# M1: the shipped image's actual commands and curl version.
docker run --rm --entrypoint /bin/sh "$SEAWEEDFS_IMAGE" -c 'command -v weed; command -v curl; curl --version' > "$ENV.commands"
python3 - "$ENV.commands" <<'PY'
import re,sys
text=open(sys.argv[1]).read()
version=re.search(r'curl (\d+)\.(\d+)\.(\d+)',text)
assert version and tuple(map(int,version.groups())) >= (7,75,0), text
PY
rm "$ENV.commands"
echo 'PASS M1 image commands'
compose up -d --wait --wait-timeout 120 comms-seaweedfs
# M2
[[ "$(docker exec comms-seaweedfs curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:8333/healthz)" == 200 ]]
echo 'PASS M2 health'
# Init uses the example's namespace sharing and has no credentials. Start the named service
# too, so the smoke exercises the one-shot's depends_on/restart wiring, not just a copy.
compose up -d comms-seaweedfs-init
for i in {1..40}; do
  state=$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}} {{.RestartCount}}' comms-seaweedfs-init)
  [[ "$state" == 'exited 0 0' ]] && break
  [[ "$state" != *' 0' || "$state" == 'exited 1 0' ]] && { echo "Init failed: $state" >&2; exit 1; }
  sleep 1
done
[[ "$state" == 'exited 0 0' ]]
unsigned() { docker exec comms-seaweedfs curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:8333$1"; }
signed() {
  docker exec comms-seaweedfs curl -sS --aws-sigv4 'aws:amz:us-east-1:s3' --user "$ACCESS:$SECRET" "$@"
}
code() { signed -o /dev/null -w '%{http_code}' "$@"; }
BASE=http://127.0.0.1:8333/comms-journal
# M3
[[ "$(unsigned '/comms-journal?list-type=2')" == 403 ]]
echo 'PASS M3 anonymous list denied'
# M4
[[ "$(code -I "$BASE")" == 200 ]]
[[ "$(code "$BASE?list-type=2")" == 200 ]]
[[ "$(code -X PUT --data-binary 'synthetic-object' "$BASE/smoke-object")" == 200 ]]
[[ "$(code "$BASE/smoke-object")" == 200 ]]
[[ "$(signed "$BASE/smoke-object")" == synthetic-object ]]
[[ "$(code -X DELETE "$BASE/smoke-object")" == 204 ]]
echo 'PASS M4 signed object verbs'
# M5: no bucket or IAM administration from the runtime identity.
[[ "$(code -X PUT --data-binary '{"Version":"2012-10-17","Statement":[]}' "$BASE?policy")" == 403 ]]
[[ "$(code -X PUT -H 'x-amz-acl: public-read' "$BASE?acl")" == 403 ]]
[[ "$(code -X DELETE "$BASE")" == 403 ]]
[[ "$(code -X PUT http://127.0.0.1:8333/another-bucket)" == 403 ]]
iam=$(code -X POST 'http://127.0.0.1:8333/?Action=CreateAccessKey')
[[ "$iam" != 2?? ]]
echo 'PASS M5 administration denied'
# M6: every listening socket plus negative controls from a network peer.
docker exec comms-seaweedfs /bin/sh -c 'command -v netstat; command -v nc' >/dev/null
docker exec comms-seaweedfs netstat -ltn > "$ENV.listeners"
python3 - "$ENV.listeners" <<'PY'
import sys
seen=set()
for line in open(sys.argv[1]):
    if not line.startswith('tcp'): continue
    address=line.split()[3]
    host,port=address.rsplit(':',1); port=int(port)
    # Approved #397 exception: Docker's embedded DNS is not an application listener.
    if host == '127.0.0.11': continue
    assert host == '127.0.0.1' or (host in ('0.0.0.0','::','[::]') and port in (8333,18333)), address
    if host in ('0.0.0.0','::','[::]'): seen.add(port)
assert {8333,18333} <= seen
PY
rm "$ENV.listeners"
# Use the actual IPv4 address: DNS or an IPv6-only socket cannot satisfy these positive probes.
IPV4=$(docker inspect -f "{{with index .NetworkSettings.Networks \"$NETWORK\"}}{{.IPAddress}}{{end}}" comms-seaweedfs)
[[ -n "$IPV4" && "$IPV4" != *:* ]]
docker run --rm --network "$NETWORK" --entrypoint /bin/sh "$SEAWEEDFS_IMAGE" -c '
  for port in 8333 18333; do
    nc -z -w 1 "$1" "$port" || { echo "S3 IPv4 listener unreachable $port" >&2; exit 1; }
  done
  for port in 9333 19333 8080 18080 8888 18888 8181 9101 6060 2022 7333; do
    if nc -z -w 1 comms-seaweedfs "$port"; then echo "unexpected listener $port" >&2; exit 1; fi
  done' sh "$IPV4"
echo 'PASS M6 listener boundary'
# M7: repeat init twice around an object and prove its bytes survive.
compose run -T --rm --no-deps comms-seaweedfs-init </dev/null
[[ "$(code -X PUT --data-binary 'preserved-object' "$BASE/keep-object")" == 200 ]]
compose run -T --rm --no-deps comms-seaweedfs-init </dev/null
[[ "$(signed "$BASE/keep-object")" == preserved-object ]]
echo 'PASS M7 init idempotence'
# M8: invalid inputs exit before weed. Each uses a separate no-network container.
for key in FLEET_COMMS_MEDIA_SECRET_KEY WEED_JWT_FILER_SIGNING_KEY FLEET_COMMS_MEDIA_BUCKET; do
  value=''; [[ "$key" != FLEET_COMMS_MEDIA_BUCKET ]] || value=Bad_Bucket
  rc=0
  output=$(docker run --name comms397-invalid --network none --entrypoint /bin/sh \
    -v "$ROOT/deploy/comms-seaweedfs:/comms-seaweedfs:ro" \
    -e FLEET_COMMS_MEDIA_BUCKET=comms-journal -e "FLEET_COMMS_MEDIA_ACCESS_KEY=$ACCESS" \
    -e "FLEET_COMMS_MEDIA_SECRET_KEY=$SECRET" -e "WEED_JWT_FILER_SIGNING_KEY=$SIGNING" \
    -e "$key=$value" "$SEAWEEDFS_IMAGE" /comms-seaweedfs/start.sh 2>&1) || rc=$?
  [[ "$rc" == 1 && "$output" == *"start.sh: $key "* ]]
  [[ "$(docker inspect -f '{{.State.Running}}' comms397-invalid)" == false ]]
  docker rm comms397-invalid >/dev/null
done
echo 'PASS M8 invalid inputs do not start'
# M9: real gRPC handlers, both absent and invalid metadata. Reflection discovers exact types.
for metadata in absent invalid; do
  headers=(); [[ "$metadata" != invalid ]] || headers=(-H 'authorization: Bearer invalid')
  for method in PutIdentity PutPolicy LifecycleDelete; do
    case "$method" in
      PutIdentity) service=messaging_pb.SeaweedS3IamCache; data='{"identity":{"name":"intruder","actions":["Admin"],"credentials":[{"accessKey":"intruder-access","secretKey":"intruder-secret"}]}}' ;;
      PutPolicy) service=messaging_pb.SeaweedS3IamCache; data='{"name":"intruder-policy","content":"{\"Version\":\"2012-10-17\",\"Statement\":[]}"}' ;;
      LifecycleDelete) service=s3_lifecycle_pb.SeaweedS3LifecycleInternal; data='{"bucket":"comms-journal","objectPath":"keep-object"}' ;;
    esac
    rc=0
    output=$(docker run --rm --network "$NETWORK" "$GRPCURL_IMAGE" -plaintext "${headers[@]}" -d "$data" comms-seaweedfs:18333 "$service/$method" 2>&1) || rc=$?
    [[ "$rc" != 0 && "$output" == *'Code: Unauthenticated'* ]]
  done
done
intruder=$(docker exec comms-seaweedfs curl -sS --aws-sigv4 'aws:amz:us-east-1:s3' --user 'intruder-access:intruder-secret' -w '\n%{http_code}' "$BASE?list-type=2")
[[ "$intruder" == *InvalidAccessKeyId* && "$intruder" == *$'\n403' ]]
[[ "$(unsigned '/')" == 403 ]]
[[ "$(signed "$BASE/keep-object")" == preserved-object ]]
echo 'PASS M9 unauthenticated gRPC cannot gain Admin'

# X9: the Comms content route uses THIS scoped runtime identity, never the Admin fixture.
: "${DOTNET_TEST_IMAGE:?CI must provide the matching .NET test SDK image}"
: "${COMMS_SCOPED_TEST_ASSEMBLY:?CI must build the scoped integration assembly}"
[[ -f "$COMMS_SCOPED_TEST_ASSEMBLY" ]]
docker pull "$DOTNET_TEST_IMAGE"
mkdir -p "$FIXTURE/testresults"
export FLEET_COMMS_SCOPED_ACCESS_KEY="$ACCESS" FLEET_COMMS_SCOPED_SECRET_KEY="$SECRET"
docker run --rm --network "$NETWORK" --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 \
  -e FLEET_COMMS_SCOPED_ENDPOINT=http://comms-seaweedfs:8333 \
  -e FLEET_COMMS_SCOPED_BUCKET=comms-journal \
  -e FLEET_COMMS_SCOPED_ACCESS_KEY -e FLEET_COMMS_SCOPED_SECRET_KEY \
  -v "$(dirname "$COMMS_SCOPED_TEST_ASSEMBLY"):/tests:ro" \
  -v "$FIXTURE/testresults:/results" "$DOTNET_TEST_IMAGE" \
  dotnet vstest /tests/Fleet.Conversations.Tests.dll \
  --TestCaseFilter:FullyQualifiedName~JournalScopedAttachmentStoreTests \
  '--logger:trx;LogFileName=scoped.trx' --ResultsDirectory:/results
python3 - "$FIXTURE/testresults/scoped.trx" <<'PYSCOPED'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
counts = root.find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters').attrib
assert counts['executed'] == counts['passed'] == '1', counts
assert counts['notExecuted'] == '0', counts
print('PASS X9 scoped-store content route: one executed, zero failures/skips')
PYSCOPED
