#!/usr/bin/env bash
# Stub-only check of scripts/run-workflow-failure-real-broker.sh (#488): a fake `docker` on
# PATH, no real container, broker or test. Each case asserts the exit status, that cleanup
# removed exactly what the recipe created, and that no host port was published.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RECIPE="$ROOT/scripts/run-workflow-failure-real-broker.sh"
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/bin"

cat > "$DIR/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >> "$CALLS"
case "$1 ${2:-}" in
  "network create") exit "${FAKE_NETWORK_RC:-0}" ;;
  "network rm") exit 0 ;;
  "rm -f") exit 0 ;;
  "exec "*) exit "${FAKE_PING_RC:-0}" ;;
  "run -d") exit "${FAKE_BROKER_RC:-0}" ;;
  "run --rm") printf '%s\n' "${FAKE_TEST_OUTPUT:-}"; exit "${FAKE_TEST_RC:-0}" ;;
esac
exit 99
FAKE
chmod +x "$DIR/bin/docker"

PASSED='  Passed Fleet.Temporal.Tests.Services.WorkflowFailureNotifierRealBrokerTests.RealAmqp_UnreachableFirstAttemptThenDelivery_RestartSendsNothing_NoSecrets [41 s]'
SKIPPED='  Skipped Fleet.Temporal.Tests.Services.WorkflowFailureNotifierRealBrokerTests.RealAmqp_UnreachableFirstAttemptThenDelivery_RestartSendsNothing_NoSecrets [1 ms]'

# case <name> <expected exit> <expect broker removed: yes|no> <expect network removed: yes|no> [env...]
case_() {
  local name=$1 want=$2 broker=$3 net=$4; shift 4
  export CALLS="$DIR/calls-$name"; : > "$CALLS"
  local rc=0
  env PATH="$DIR/bin:$PATH" READY_SECONDS=3 "$@" bash "$RECIPE" > "$DIR/out-$name" 2>&1 || rc=$?
  [[ $rc -eq $want ]] || { echo "FAIL $name: exit $rc, wanted $want"; cat "$DIR/out-$name"; exit 1; }
  local removed_broker=no removed_net=no
  grep -Eq '^docker rm -f phleet488-[0-9]+-[0-9]+-rabbit$' "$CALLS" && removed_broker=yes
  grep -Eq '^docker network rm phleet488-[0-9]+-[0-9]+-net$' "$CALLS" && removed_net=yes
  [[ $removed_broker == "$broker" && $removed_net == "$net" ]] || { echo "FAIL $name: cleanup broker=$removed_broker net=$removed_net"; cat "$CALLS"; exit 1; }
  ! grep -Eq -- ' (-p|--publish)( |=)' "$CALLS" || { echo "FAIL $name: published a host port"; exit 1; }
  echo "PASS $name"
}

case_ network-create-fails 5 no no FAKE_NETWORK_RC=5
case_ broker-start-fails 23 no yes FAKE_BROKER_RC=23
case_ readiness-times-out 70 yes yes FAKE_PING_RC=1
case_ test-fails 42 yes yes FAKE_TEST_RC=42 "FAKE_TEST_OUTPUT=$PASSED"
case_ test-skipped 3 yes yes FAKE_TEST_RC=0 "FAKE_TEST_OUTPUT=$SKIPPED"
case_ no-test-matched 3 yes yes FAKE_TEST_RC=0 FAKE_TEST_OUTPUT=
case_ test-passes 0 yes yes FAKE_TEST_RC=0 "FAKE_TEST_OUTPUT=$PASSED"
grep -q '^RESULT: PASS$' "$DIR/out-test-passes"
grep -q 'RESULT: FAIL (exit 42)' "$DIR/out-test-fails"
echo 'PASS recipe reports the test status through owned cleanup'
