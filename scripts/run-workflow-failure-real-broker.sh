#!/usr/bin/env bash
# Operator-run real-AMQP acceptance for the workflow failure notifier (#488).
#
# Runs WorkflowFailureNotifierRealBrokerTests from one reviewed commit. The SDK container's
# only input is `git archive <sha>`: tracked files of that commit, never ignored, untracked or
# uncommitted files. Creates its own Docker network, RabbitMQ and SDK container (no host port,
# no bind mount), removes each and confirms it is gone. Never points at a broker a real bridge uses.
#
#   bash scripts/run-workflow-failure-real-broker.sh <reviewed-commit-sha>
#
# Exit: the test's status, never masked by cleanup; 2 bad or unknown commit, or this recipe is
# not that commit's copy; 3 skipped or unmatched test; 70 broker not ready; 124 test timed out;
# 4 an owned resource is left after an otherwise passing run.
# Optional: BROKER_IMAGE, SDK_IMAGE, READY_SECONDS (default 90), TEST_SECONDS (default 1800),
# FLEET_TEST_RABBITMQ_UNREACHABLE_PORT (default 1).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RECIPE=scripts/run-workflow-failure-real-broker.sh
BROKER_IMAGE=${BROKER_IMAGE:-rabbitmq:3-management}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
READY_SECONDS=${READY_SECONDS:-90}
TEST_SECONDS=${TEST_SECONDS:-1800}
RUN="phleet488-$(date +%s)-$$"
NET="$RUN-net"
BROKER="$RUN-rabbit"
SDK="$RUN-sdk"
FILTER="FullyQualifiedName~WorkflowFailureNotifierRealBrokerTests"
TEST_NAME="Fleet.Temporal.Tests.Services.WorkflowFailureNotifierRealBrokerTests"
WORK=""
owned=() # creation order: "net:<id>" or "cid:<cidfile>"

# Removes one owned resource and succeeds only if a listing confirms it is gone.
remove_owned() {
  local kind=${1%%:*} ref=${1#*:} id left
  if [[ $kind == net ]]; then
    id=$ref
    docker network rm "$id" >/dev/null 2>&1 || true
    left=$(docker network ls -q --no-trunc --filter "id=$id") || return 1
  else
    [[ -s $ref ]] || return 0 # docker writes the cidfile only once the container exists
    id=$(<"$ref")
    docker rm -f -v "$id" >/dev/null 2>&1 || true
    left=$(docker ps -a -q --no-trunc --filter "id=$id") || return 1
  fi
  [[ -z $left ]]
}

cleanup() {
  local rc=$? i unresolved=()
  trap - EXIT
  # Newest first: the SDK and broker containers, then the network they are attached to.
  for ((i = ${#owned[@]} - 1; i >= 0; i--)); do
    remove_owned "${owned[$i]}" || unresolved+=("${owned[$i]}")
  done
  if [[ -n $WORK ]]; then
    rm -rf "$WORK"
    [[ ! -e $WORK ]] || unresolved+=("dir:$WORK")
  fi
  if ((${#unresolved[@]})); then
    echo "CLEANUP: UNRESOLVED ${unresolved[*]}" >&2
    ((rc != 0)) || rc=4
  fi
  if ((rc == 0)); then echo "RESULT: PASS"; else echo "RESULT: FAIL (exit $rc)" >&2; fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

SHA=${1:-}
if [[ $# -ne 1 || ! $SHA =~ ^[0-9a-f]{40}$ ]]; then
  echo "usage: $0 <reviewed-commit-sha (40 lowercase hex)>" >&2
  exit 2
fi
if ! git -C "$ROOT" rev-parse --verify --quiet "$SHA^{commit}" >/dev/null; then
  echo "commit $SHA is not in $ROOT; fetch it first" >&2
  exit 2
fi
if ! git -C "$ROOT" show "$SHA:$RECIPE" | cmp -s - "${BASH_SOURCE[0]}"; then
  echo "this recipe differs from $SHA:$RECIPE; run the reviewed copy" >&2
  exit 2
fi
TREE=$(git -C "$ROOT" rev-parse "$SHA^{tree}")

WORK=$(mktemp -d)
git -C "$ROOT" archive --format=tar -o "$WORK/source.tar" "$SHA"
echo "SOURCE: commit $SHA tree $TREE (git archive, tracked files only)"

NET_ID=$(docker network create "$NET")
owned+=("net:$NET_ID")
# The image declares a volume for /var/lib/rabbitmq; tmpfs keeps that from becoming an anonymous volume.
owned+=("cid:$WORK/broker.cid")
docker run -d --cidfile "$WORK/broker.cid" --name "$BROKER" --network "$NET" \
  --mount type=tmpfs,dst=/var/lib/rabbitmq "$BROKER_IMAGE" >/dev/null

ready=false
deadline=$((SECONDS + READY_SECONDS))
while ((SECONDS < deadline)); do
  if docker exec "$BROKER" rabbitmq-diagnostics -q ping >/dev/null 2>&1; then ready=true; break; fi
  sleep 2
done
if ! $ready; then
  echo "broker $BROKER not ready within ${READY_SECONDS}s" >&2
  exit 70
fi

# The archive arrives on stdin; `timeout` bounds the whole container run. The test must be
# reported as passed: a skip (no broker variable) or zero matches fails.
owned+=("cid:$WORK/sdk.cid")
set +e
docker run -i --cidfile "$WORK/sdk.cid" --name "$SDK" --network "$NET" \
  -e FLEET_TEST_RABBITMQ_HOST="$BROKER" \
  -e FLEET_TEST_RABBITMQ_UNREACHABLE_PORT="${FLEET_TEST_RABBITMQ_UNREACHABLE_PORT:-1}" \
  "$SDK_IMAGE" timeout --kill-after=30 "$TEST_SECONDS" bash -c \
  'mkdir -p /work && tar -x -C /work && cd /work && dotnet test tests/Fleet.Temporal.Tests -c Release --filter "$0" --logger "console;verbosity=normal"' \
  "$FILTER" <"$WORK/source.tar" | tee "$WORK/test.log"
status=${PIPESTATUS[0]}
set -e
if [[ $status -eq 0 ]] && ! grep -q "Passed ${TEST_NAME}\." "$WORK/test.log"; then
  echo "real-broker test did not pass (skipped or not run)" >&2
  status=3
fi
exit "$status"
