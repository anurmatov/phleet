#!/usr/bin/env bash
# fleet-memory's Qdrant store against a real Qdrant.
#
# Every other memory test uses an in-memory stand-in for the index. The tests this runs
# (tests/Fleet.Memory.Tests, the classes named *Qdrant*) are the real VectorStore and the real
# requests against a real server: sectioned upserts, both sides of a search under every filter,
# listings, the removal of stale and orphaned points, and what a start reads back.
#
# Starts one Qdrant container of the release docker-compose.example.yml pins, or QDRANT_VERSION,
# on loopback ports Docker picks; runs the tests against it; refuses a run in which any of them was
# skipped or none ran. Cleanup removes only the container this run created, by name.
#
#   bash tests/memory-qdrant/run.sh
#   QDRANT_VERSION=v1.19.1 bash tests/memory-qdrant/run.sh
#   CONFIGURATION=Debug bash tests/memory-qdrant/run.sh
#
# Exit codes: 0 every test ran and passed, 1 a test failed or was skipped, 2 infrastructure.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
RUN="memqdrant-$(date +%s)-$$"
RESULTS="$(mktemp -d)"

fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }

cleanup() {
  local rc=$?
  docker rm -f "$RUN" >/dev/null 2>&1 || echo "WARN: cleanup could not remove $RUN"
  rm -rf "$RESULTS"
  exit "$rc"
}
trap cleanup EXIT

PINNED="$(sed -n 's/.*image: qdrant\/qdrant:\${FLEET_QDRANT_VERSION:-\([^}]*\)}.*/\1/p' "$REPO_ROOT/docker-compose.example.yml" | head -1)"
[ -n "$PINNED" ] || fail_infra "no Qdrant pin found in docker-compose.example.yml"
VERSION="${QDRANT_VERSION:-$PINNED}"
echo "Qdrant $VERSION (docker-compose.example.yml pins $PINNED)"

docker run -d --name "$RUN" -p 127.0.0.1::6333 -p 127.0.0.1::6334 "qdrant/qdrant:$VERSION" >/dev/null \
  || fail_infra "could not start qdrant/qdrant:$VERSION"
HTTP_PORT="$(docker port "$RUN" 6333/tcp | head -1 | sed 's/.*://')"
GRPC_PORT="$(docker port "$RUN" 6334/tcp | head -1 | sed 's/.*://')"
[ -n "$HTTP_PORT" ] && [ -n "$GRPC_PORT" ] || fail_infra "no published ports for $RUN"

REPORTED=""
for _ in $(seq 1 60); do
  REPORTED="$(curl -fsS -m 2 "http://127.0.0.1:$HTTP_PORT/" 2>/dev/null | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' || true)"
  [ -n "$REPORTED" ] && break
  sleep 1
done
[ -n "$REPORTED" ] || fail_infra "Qdrant did not answer on 127.0.0.1:$HTTP_PORT"
[ "v$REPORTED" = "$VERSION" ] || fail_infra "asked for $VERSION, the server says v$REPORTED"

set +e
FLEET_MEMORY_TEST_QDRANT_URL="http://127.0.0.1:$GRPC_PORT" dotnet test "$REPO_ROOT/tests/Fleet.Memory.Tests" \
  --configuration "$CONFIGURATION" \
  --filter "FullyQualifiedName~Qdrant" \
  --logger "console;verbosity=normal" \
  --logger "trx;LogFileName=memory-qdrant.trx" \
  --results-directory "$RESULTS"
TEST_RC=$?
set -e

# Evidence only when every test ran against the server and passed: a skipped test is a green
# summary that proves nothing.
TRX="$RESULTS/memory-qdrant.trx"
[ -f "$TRX" ] || fail_assert "no test result file: the tests did not run (dotnet test exit $TEST_RC)"
COUNTERS="$(grep -o '<Counters[^>]*>' "$TRX" | head -1)"
read_count() { echo "$COUNTERS" | grep -o "$1=\"[0-9]*\"" | grep -o '[0-9]*' || echo ""; }
EXECUTED="$(read_count executed)"; NOT_EXECUTED="$(read_count notExecuted)"; PASSED="$(read_count passed)"; TOTAL="$(read_count total)"
if [ "$TEST_RC" -ne 0 ] || [ -z "$EXECUTED" ] || [ "$EXECUTED" -eq 0 ] || [ "$PASSED" -ne "$EXECUTED" ] || [ "$EXECUTED" -ne "$TOTAL" ] || [ "${NOT_EXECUTED:-0}" != "0" ]; then
  fail_assert "Qdrant $VERSION: total=$TOTAL executed=$EXECUTED passed=$PASSED notExecuted=$NOT_EXECUTED (dotnet test exit $TEST_RC)"
fi
echo "PASS Qdrant $VERSION: $PASSED test(s) executed against the server, 0 skipped."
