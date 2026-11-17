#!/usr/bin/env bash
# fleet-memory's Qdrant client against a real Qdrant that requires TLS and an API key.
#
# run.sh beside this is the store against a Qdrant that takes no credential, as the example
# deployment's does. This is the other configuration fleet-memory supports: Qdrant__Url with an
# https scheme and Qdrant__ApiKey. The tests it runs (tests/Fleet.Memory.Tests, the class
# SecuredIndexTests) are the real VectorStore built from those two settings, against a server that
# answers only over TLS and only to its key.
#
# What it sets up, all of it made for the run and removed after it:
#   - a certificate authority and a server certificate for "localhost" (openssl; nothing committed);
#   - one Qdrant container of the release docker-compose.example.yml pins, or QDRANT_VERSION, with
#     that certificate and a random API key, publishing no port;
#   - one .NET SDK container in the Qdrant container's network namespace, where the tests run. It is
#     given the authority as a trusted root through SSL_CERT_FILE, which .NET reads on Linux. That is
#     the platform's normal certificate validation with one more root; nothing in the client or in
#     the tests relaxes validation. (A container, not this machine's dotnet, because macOS and
#     Windows have no such variable: there the authority would have to go into the user's keychain.)
#
# Before the tests it checks that the server is what it claims to be: it answers with the key over
# TLS, refuses without the key, and does not answer in plaintext. It refuses a run in which a test
# was skipped or none ran. Nothing is bind-mounted; files go in and out with docker cp.
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
TESTS="$RUN-tests"
WORK="$(mktemp -d)"

fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }

cleanup() {
  local rc=$?
  docker rm -f "$TESTS" >/dev/null 2>&1 || true
  docker rm -f "$RUN" >/dev/null 2>&1 || echo "WARN: cleanup could not remove $RUN"
  rm -rf "$WORK"
  exit "$rc"
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
docker create --name "$RUN" \
  -e QDRANT__SERVICE__API_KEY="$API_KEY" \
  -e QDRANT__SERVICE__ENABLE_TLS=true \
  -e QDRANT__TLS__CERT=/qdrant/tls/cert.pem \
  -e QDRANT__TLS__KEY=/qdrant/tls/key.pem \
  "qdrant/qdrant:$VERSION" >/dev/null || fail_infra "could not create qdrant/qdrant:$VERSION"
docker cp "$SERVER" "$RUN:/qdrant/tls" >/dev/null || fail_infra "could not give $RUN its certificate"
docker start "$RUN" >/dev/null || fail_infra "could not start $RUN"

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

cd /tmp/fleet
# Built before the authority is given, so the package feed is checked against the system roots alone.
dotnet build tests/Fleet.Memory.Tests --configuration "$CONFIGURATION" --nologo -v quiet \
  || fail_infra "the tests did not build"

set +e
SSL_CERT_FILE="$CA" \
FLEET_MEMORY_TEST_SECURED_URL="https://localhost:6334" \
FLEET_MEMORY_TEST_SECURED_API_KEY="$API_KEY" \
  dotnet test tests/Fleet.Memory.Tests --no-build --configuration "$CONFIGURATION" \
    --filter "FullyQualifiedName~SecuredIndex" \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=secured.trx" \
    --results-directory /tmp/results
echo "$?" > /tmp/results/dotnet-test-exit-code
INSIDE

docker create --name "$TESTS" --network "container:$RUN" \
  -e API_KEY="$API_KEY" -e EXPECTED_VERSION="$VERSION" -e CONFIGURATION="$CONFIGURATION" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$SDK_IMAGE" bash /tmp/fleet/run-secured-tests.sh >/dev/null || fail_infra "could not create the test container from $SDK_IMAGE"
docker cp "$STAGE" "$TESTS:/tmp/fleet" >/dev/null || fail_infra "could not copy the sources into $TESTS"

set +e
docker start -a "$TESTS"
INSIDE_RC=$?
set -e
[ "$INSIDE_RC" -ne 2 ] || exit 2

# Evidence only when every test ran against the server and passed: a skipped test is a green
# summary that proves nothing.
docker cp "$TESTS:/tmp/results" "$WORK/results" >/dev/null 2>&1 || fail_assert "no test results: the tests did not run (exit $INSIDE_RC)"
TRX="$WORK/results/secured.trx"
[ -f "$TRX" ] || fail_assert "no test result file: the tests did not run (exit $INSIDE_RC)"
TEST_RC="$(cat "$WORK/results/dotnet-test-exit-code" 2>/dev/null || echo "")"
COUNTERS="$(grep -o '<Counters[^>]*>' "$TRX" | head -1)"
read_count() { echo "$COUNTERS" | grep -o "$1=\"[0-9]*\"" | grep -o '[0-9]*' || echo ""; }
EXECUTED="$(read_count executed)"; NOT_EXECUTED="$(read_count notExecuted)"; PASSED="$(read_count passed)"; TOTAL="$(read_count total)"
if [ "$TEST_RC" != 0 ] || [ -z "$EXECUTED" ] || [ "$EXECUTED" -eq 0 ] || [ "$PASSED" -ne "$EXECUTED" ] || [ "$EXECUTED" -ne "$TOTAL" ] || [ "${NOT_EXECUTED:-0}" != "0" ]; then
  fail_assert "Qdrant $VERSION with TLS and a key: total=$TOTAL executed=$EXECUTED passed=$PASSED notExecuted=$NOT_EXECUTED (dotnet test exit $TEST_RC)"
fi
echo "PASS Qdrant $VERSION with TLS and an API key: $PASSED test(s) executed against the server, 0 skipped."
