#!/usr/bin/env bash
# Operator-run real-AMQP acceptance for the workflow failure notifier (#488).
#
# Creates its own disposable Docker network and RabbitMQ (no host port), runs
# WorkflowFailureNotifierRealBrokerTests in an SDK container on that network, and
# always removes exactly what it created. Exits with the test's status; a skipped or
# missing test is a failure. Never points at a broker a real bridge uses.
#
#   bash scripts/run-workflow-failure-real-broker.sh
#
# Optional: BROKER_IMAGE, SDK_IMAGE, READY_SECONDS (default 90),
# FLEET_TEST_RABBITMQ_UNREACHABLE_PORT (default 1).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
BROKER_IMAGE=${BROKER_IMAGE:-rabbitmq:3-management}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
READY_SECONDS=${READY_SECONDS:-90}
RUN="phleet488-$(date +%s)-$$"
NET="$RUN-net"
BROKER="$RUN-rabbit"
FILTER="FullyQualifiedName~WorkflowFailureNotifierRealBrokerTests"
TEST_NAME="Fleet.Temporal.Tests.Services.WorkflowFailureNotifierRealBrokerTests"
created_net=false
created_broker=false
log=""

cleanup() {
  local rc=$?
  trap - EXIT
  if $created_broker; then docker rm -f "$BROKER" >/dev/null 2>&1 || echo "WARN: could not remove $BROKER" >&2; fi
  if $created_net; then docker network rm "$NET" >/dev/null 2>&1 || echo "WARN: could not remove $NET" >&2; fi
  [[ -n "$log" ]] && rm -f "$log"
  if [[ $rc -eq 0 ]]; then echo "RESULT: PASS"; else echo "RESULT: FAIL (exit $rc)" >&2; fi
  exit "$rc"
}
trap cleanup EXIT

docker network create "$NET" >/dev/null
created_net=true
docker run -d --name "$BROKER" --network "$NET" "$BROKER_IMAGE" >/dev/null
created_broker=true

ready=false
deadline=$((SECONDS + READY_SECONDS))
while (( SECONDS < deadline )); do
  if docker exec "$BROKER" rabbitmq-diagnostics -q ping >/dev/null 2>&1; then ready=true; break; fi
  sleep 2
done
if ! $ready; then
  echo "broker $BROKER not ready within ${READY_SECONDS}s" >&2
  exit 70
fi

# The repo is mounted read-only and copied inside the container, so nothing is written to
# it. The test must be reported as passed: a skip (no broker variable) or zero matches fails.
log=$(mktemp)
set +e
docker run --rm --network "$NET" \
  -e FLEET_TEST_RABBITMQ_HOST="$BROKER" \
  -e FLEET_TEST_RABBITMQ_UNREACHABLE_PORT="${FLEET_TEST_RABBITMQ_UNREACHABLE_PORT:-1}" \
  --mount "type=bind,src=$ROOT,dst=/src,readonly" \
  "$SDK_IMAGE" bash -c 'cp -a /src/. /work && cd /work && dotnet test tests/Fleet.Temporal.Tests -c Release --filter "$0" --logger "console;verbosity=normal"' \
  "$FILTER" | tee "$log"
status=${PIPESTATUS[0]}
set -e
if [[ $status -eq 0 ]] && ! grep -q "Passed ${TEST_NAME}\." "$log"; then
  echo "real-broker test did not pass (skipped or not run)" >&2
  status=3
fi
exit "$status"
