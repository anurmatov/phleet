#!/usr/bin/env bash
# fleet-memory's Qdrant client against real Qdrants that require an API key: one over TLS, one over
# plaintext reached by an explicit http URL.
#
# run.sh beside this is the store against a Qdrant that takes no credential, as the example
# deployment's does. These are the other configurations fleet-memory supports: Qdrant__ApiKey with
# an https Qdrant__Url, and with an explicit http one for an operator-trusted private network (the
# key is then not encrypted). The tests it runs (tests/Fleet.Memory.Tests, the classes
# SecuredIndexTests and SecuredIndexOverHttpTests) are the real VectorStore built from those two
# settings, against servers that answer only to their key, the first only over TLS.
#
# What it sets up, all of it made for the run and removed after it:
#   - a certificate authority and a server certificate for "localhost" (openssl; nothing committed);
#   - one Qdrant container of the release docker-compose.example.yml pins, or QDRANT_VERSION, with
#     that certificate and a random API key, publishing no port;
#   - a second Qdrant of the same release in the first one's network namespace, on ports 6343/6344,
#     with another random API key and no TLS;
#   - one .NET SDK container in the Qdrant container's network namespace, where the tests run. It is
#     given the authority as a trusted root through SSL_CERT_FILE, which .NET reads on Linux. That is
#     the platform's normal certificate validation with one more root; nothing in the client or in
#     the tests relaxes validation. (A container, not this machine's dotnet, because macOS and
#     Windows have no such variable: there the authority would have to go into the user's keychain.)
#
# Before the tests it checks that each server is what it claims to be: the TLS one answers with the
# key over TLS, refuses without the key, and does not answer in plaintext; the http one answers with
# its key and refuses without it and with another. It refuses a run in which a test was skipped or
# none ran. Nothing is bind-mounted; files go in and out with docker cp.
#
#   bash tests/memory-qdrant/secured.sh
#   QDRANT_VERSION=v1.19.1 bash tests/memory-qdrant/secured.sh
#
# Exit codes: 0 every test ran and passed, 1 a test failed or was skipped, 2 infrastructure.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
SDK_IMAGE="${DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
RUN="memqdrant-secured-$(date +%s)-$$"
HTTP_SERVER="$RUN-http"
TESTS="$RUN-tests"
WORK="$(mktemp -d)"

fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }

# The IDs `docker create` returned for this run, and nothing else, are what cleanup removes: a name
# another container already holds makes the create fail, and that container is never touched.
TESTS_ID="" HTTP_ID="" RUN_ID=""
cleanup() {
  local rc=$?
  [ -z "$TESTS_ID" ] || docker rm -f "$TESTS_ID" >/dev/null 2>&1 || true
  [ -z "$HTTP_ID" ] || docker rm -f "$HTTP_ID" >/dev/null 2>&1 || echo "WARN: cleanup could not remove $HTTP_SERVER ($HTTP_ID)"
  [ -z "$RUN_ID" ] || docker rm -f "$RUN_ID" >/dev/null 2>&1 || echo "WARN: cleanup could not remove $RUN ($RUN_ID)"
  rm -rf "$WORK"
  exit "$rc"
}
# create_owned <var> <docker create args…>: the new container's full ID into <var>, or exit 2.
create_owned() {
  local var=$1 id; shift
  id="$(docker create "$@")" && [[ $id =~ ^[0-9a-f]{64}$ ]] || return 1
  printf -v "$var" '%s' "$id"
}
trap cleanup EXIT

command -v openssl >/dev/null || fail_infra "openssl is needed to make the run's certificates"

PINNED="$(sed -n 's/.*image: qdrant\/qdrant:\${FLEET_QDRANT_VERSION:-\([^}]*\)}.*/\1/p' "$REPO_ROOT/docker-compose.example.yml" | head -1)"
[ -n "$PINNED" ] || fail_infra "no Qdrant pin found in docker-compose.example.yml"
VERSION="${QDRANT_VERSION:-$PINNED}"
echo "Qdrant $VERSION with TLS and an API key (docker-compose.example.yml pins $PINNED)"

# ── The run's authority, server certificate and key ───────────────────────────
TLS="$WORK/tls"
mkdir -p "$TLS"
API_KEY="memqdrant-$(openssl rand -hex 24)"
HTTP_API_KEY="memqdrant-http-$(openssl rand -hex 24)"
{
  openssl ecparam -name prime256v1 -genkey -noout -out "$TLS/ca-key.pem"
  printf '[req]\ndistinguished_name=dn\nx509_extensions=ca\nprompt=no\n[dn]\nCN=memory-qdrant test authority\n[ca]\nbasicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign\nsubjectKeyIdentifier=hash\n' > "$TLS/ca.cnf"
  openssl req -x509 -new -key "$TLS/ca-key.pem" -sha256 -days 2 -config "$TLS/ca.cnf" -out "$TLS/ca.pem"
  openssl ecparam -name prime256v1 -genkey -noout -out "$TLS/key.pem"
  openssl req -new -key "$TLS/key.pem" -subj "/CN=localhost" -out "$TLS/server.csr"
  printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\n' > "$TLS/server.ext"
  openssl x509 -req -in "$TLS/server.csr" -CA "$TLS/ca.pem" -CAkey "$TLS/ca-key.pem" -CAcreateserial \
    -sha256 -days 2 -extfile "$TLS/server.ext" -out "$TLS/cert.pem"
} >/dev/null 2>&1 || fail_infra "openssl could not make the run's certificates"
# The server reads these as whatever user its image runs as. They are this run's alone.
SERVER="$WORK/server-tls"
mkdir -p "$SERVER"
cp "$TLS/cert.pem" "$TLS/key.pem" "$SERVER/"
chmod 755 "$SERVER"
chmod 644 "$SERVER/cert.pem" "$SERVER/key.pem"

# ── Qdrant: TLS on, a key required, no published port ─────────────────────────
create_owned RUN_ID --name "$RUN" \
  -e QDRANT__SERVICE__API_KEY="$API_KEY" \
  -e QDRANT__SERVICE__ENABLE_TLS=true \
  -e QDRANT__TLS__CERT=/qdrant/tls/cert.pem \
  -e QDRANT__TLS__KEY=/qdrant/tls/key.pem \
  "qdrant/qdrant:$VERSION" || fail_infra "could not create $RUN from qdrant/qdrant:$VERSION"
docker cp "$SERVER" "$RUN_ID:/qdrant/tls" >/dev/null || fail_infra "could not give $RUN its certificate"
docker start "$RUN_ID" >/dev/null || fail_infra "could not start $RUN"

# ── Qdrant: a key required, no TLS, beside the first (same localhost, other ports) ──
create_owned HTTP_ID --name "$HTTP_SERVER" --network "container:$RUN_ID" \
  -e QDRANT__SERVICE__API_KEY="$HTTP_API_KEY" \
  -e QDRANT__SERVICE__HTTP_PORT=6343 \
  -e QDRANT__SERVICE__GRPC_PORT=6344 \
  "qdrant/qdrant:$VERSION" || fail_infra "could not create $HTTP_SERVER from qdrant/qdrant:$VERSION"
docker start "$HTTP_ID" >/dev/null || fail_infra "could not start $HTTP_SERVER"

# ── The sources the tests are built from, without build output ────────────────
STAGE="$WORK/fleet"
mkdir -p "$STAGE"
(cd "$REPO_ROOT" && tar -cf - \
    --exclude='./src/fleet-dashboard' --exclude='bin' --exclude='obj' --exclude='node_modules' \
    ./Directory.Build.props ./Directory.Packages.props ./src ./tests/Fleet.Memory.Tests ./tests/fixtures/local-endpoint) \
  | tar -xf - -C "$STAGE" || fail_infra "could not copy the sources"
cp "$TLS/ca.pem" "$STAGE/test-authority.pem"

# What runs in the SDK container. localhost there is the Qdrant container.
cat > "$STAGE/run-secured-tests.sh" <<'INSIDE'
#!/usr/bin/env bash
set -euo pipefail
CA=/tmp/fleet/test-authority.pem
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }

reported=""
for _ in $(seq 1 60); do
  reported="$(curl -fsS -m 2 --cacert "$CA" -H "api-key: $API_KEY" https://localhost:6333/ 2>/dev/null | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' || true)"
  [ -n "$reported" ] && break
  sleep 1
done
[ -n "$reported" ] || fail_infra "Qdrant did not answer over TLS with the key on localhost:6333"
[ "v$reported" = "$EXPECTED_VERSION" ] || fail_infra "asked for $EXPECTED_VERSION, the server says v$reported"

# The server must be what this run is about, or the tests below prove nothing.
without_key="$(curl -s -m 5 -o /dev/null -w '%{http_code}' --cacert "$CA" https://localhost:6333/collections || true)"
case "$without_key" in 401|403) ;; *) fail_infra "Qdrant answered $without_key to a request without the key (expected 401 or 403)" ;; esac
with_key="$(curl -s -m 5 -o /dev/null -w '%{http_code}' --cacert "$CA" -H "api-key: $API_KEY" https://localhost:6333/collections || true)"
[ "$with_key" = 200 ] || fail_infra "Qdrant answered $with_key to a request with the key (expected 200)"
if curl -fsS -m 5 -o /dev/null http://localhost:6333/collections 2>/dev/null; then
  fail_infra "Qdrant answered in plaintext"
fi
if curl -fsS -m 5 -o /dev/null -H "api-key: $API_KEY" https://localhost:6333/collections 2>/dev/null; then
  fail_infra "the test authority is trusted without being given: the certificate check proves nothing here"
fi
echo "Qdrant v$reported: answers over TLS with the key; refuses without it ($without_key); no plaintext; its authority is not a system root."

reported_http=""
for _ in $(seq 1 60); do
  reported_http="$(curl -fsS -m 2 -H "api-key: $HTTP_API_KEY" http://localhost:6343/ 2>/dev/null | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' || true)"
  [ -n "$reported_http" ] && break
  sleep 1
done
[ -n "$reported_http" ] || fail_infra "the http Qdrant did not answer with its key on localhost:6343"
[ "v$reported_http" = "$EXPECTED_VERSION" ] || fail_infra "asked for $EXPECTED_VERSION, the http server says v$reported_http"
http_without="$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://localhost:6343/collections || true)"
case "$http_without" in 401|403) ;; *) fail_infra "the http Qdrant answered $http_without without the key (expected 401 or 403)" ;; esac
http_wrong="$(curl -s -m 5 -o /dev/null -w '%{http_code}' -H "api-key: $API_KEY" http://localhost:6343/collections || true)"
case "$http_wrong" in 401|403) ;; *) fail_infra "the http Qdrant answered $http_wrong to another server's key (expected 401 or 403)" ;; esac
http_with="$(curl -s -m 5 -o /dev/null -w '%{http_code}' -H "api-key: $HTTP_API_KEY" http://localhost:6343/collections || true)"
[ "$http_with" = 200 ] || fail_infra "the http Qdrant answered $http_with with its key (expected 200)"
echo "Qdrant v$reported_http over http: answers with its key; refuses without it ($http_without) and with another ($http_wrong)."

cd /tmp/fleet
# Built before the authority is given, so the package feed is checked against the system roots alone.
dotnet build tests/Fleet.Memory.Tests --configuration "$CONFIGURATION" --nologo -v quiet \
  || fail_infra "the tests did not build"

set +e
SSL_CERT_FILE="$CA" \
FLEET_MEMORY_TEST_SECURED_URL="https://localhost:6334" \
FLEET_MEMORY_TEST_SECURED_API_KEY="$API_KEY" \
FLEET_MEMORY_TEST_SECURED_HTTP_URL="http://localhost:6344" \
FLEET_MEMORY_TEST_SECURED_HTTP_API_KEY="$HTTP_API_KEY" \
  dotnet test tests/Fleet.Memory.Tests --no-build --configuration "$CONFIGURATION" \
    --filter "FullyQualifiedName~SecuredIndex" \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=secured.trx" \
    --results-directory /tmp/results
echo "$?" > /tmp/results/dotnet-test-exit-code
INSIDE

create_owned TESTS_ID --name "$TESTS" --network "container:$RUN_ID" \
  -e API_KEY="$API_KEY" -e HTTP_API_KEY="$HTTP_API_KEY" -e EXPECTED_VERSION="$VERSION" -e CONFIGURATION="$CONFIGURATION" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$SDK_IMAGE" bash /tmp/fleet/run-secured-tests.sh || fail_infra "could not create $TESTS from $SDK_IMAGE"
docker cp "$STAGE" "$TESTS_ID:/tmp/fleet" >/dev/null || fail_infra "could not copy the sources into $TESTS"

set +e
docker start -a "$TESTS_ID"
INSIDE_RC=$?
set -e
[ "$INSIDE_RC" -ne 2 ] || exit 2

# Evidence only when every test ran against the server and passed: a skipped test is a green
# summary that proves nothing.
docker cp "$TESTS_ID:/tmp/results" "$WORK/results" >/dev/null 2>&1 || fail_assert "no test results: the tests did not run (exit $INSIDE_RC)"
TRX="$WORK/results/secured.trx"
[ -f "$TRX" ] || fail_assert "no test result file: the tests did not run (exit $INSIDE_RC)"
TEST_RC="$(cat "$WORK/results/dotnet-test-exit-code" 2>/dev/null || echo "")"
COUNTERS="$(grep -o '<Counters[^>]*>' "$TRX" | head -1)"
read_count() { echo "$COUNTERS" | grep -o "$1=\"[0-9]*\"" | grep -o '[0-9]*' || echo ""; }
EXECUTED="$(read_count executed)"; NOT_EXECUTED="$(read_count notExecuted)"; PASSED="$(read_count passed)"; TOTAL="$(read_count total)"
if [ "$TEST_RC" != 0 ] || [ -z "$EXECUTED" ] || [ "$EXECUTED" -eq 0 ] || [ "$PASSED" -ne "$EXECUTED" ] || [ "$EXECUTED" -ne "$TOTAL" ] || [ "${NOT_EXECUTED:-0}" != "0" ]; then
  fail_assert "Qdrant $VERSION with a key, over TLS and over http: total=$TOTAL executed=$EXECUTED passed=$PASSED notExecuted=$NOT_EXECUTED (dotnet test exit $TEST_RC)"
fi
echo "PASS Qdrant $VERSION with an API key, over TLS and over http: $PASSED test(s) executed against the servers, 0 skipped."
