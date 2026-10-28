#!/usr/bin/env bash
# Stub-only check of scripts/run-workflow-failure-real-broker.sh (#488): a fake `docker` on
# PATH, no real container, broker or test. The recipe runs from a throwaway Git repo whose
# working tree carries an ignored secret, an untracked file and an uncommitted edit. Each case
# asserts the exit status, that every owned resource and temp file is gone (or reported), and
# that nothing published a host port or bind-mounted a host path.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RECIPE=scripts/run-workflow-failure-real-broker.sh
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/bin"

cat > "$DIR/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >> "$CALLS"
mode="$1 ${2:-}"
last=${!#}
stuck() { [[ " ${FAKE_STUCK:-} " == *" $1 "* ]]; }
case "$mode" in
  "network create")
    [[ ${FAKE_NETWORK_RC:-0} -eq 0 ]] || exit "$FAKE_NETWORK_RC"
    touch "$STATE/id-$3"; echo "id-$3"; exit 0 ;;
  "network rm")
    stuck net && exit 55
    rm -f "$STATE/$last"; exit 0 ;;
  "network ls"|"ps -a")
    [[ ${FAKE_LIST_RC:-0} -eq 0 ]] || exit "$FAKE_LIST_RC"
    id=${last#id=}; [[ -e $STATE/$id ]] && echo "$id"; exit 0 ;;
  "rm -f")
    [[ $last == *-rabbit ]] && stuck rabbit && exit 44
    rm -f "$STATE/$last"; exit 0 ;;
  "exec "*) exit "${FAKE_PING_RC:-0}" ;;
  "run -d"|"run -i")
    shift 2
    while (($#)); do
      case $1 in --cidfile) cid=$2; shift ;; --name) name=$2; shift ;; esac
      shift
    done
    echo "id-$name" > "$cid"; touch "$STATE/id-$name"
    [[ $mode == "run -d" ]] && exit "${FAKE_BROKER_RC:-0}"
    cat > "$INPUT"
    printf '%s\n' "${FAKE_TEST_OUTPUT:-}"; exit "${FAKE_TEST_RC:-0}" ;;
esac
exit 99
FAKE
chmod +x "$DIR/bin/docker"

# Throwaway repo: the reviewed commit, then private and unreviewed working-tree content.
REPO="$DIR/repo"
mkdir -p "$REPO/scripts"
cp "$ROOT/$RECIPE" "$REPO/$RECIPE"
printf '.env\n' > "$REPO/.gitignore"
printf 'reviewed\n' > "$REPO/tracked.txt"
git -C "$REPO" init -q
git -C "$REPO" add -A
git -C "$REPO" -c user.name=stub -c user.email=stub@example.invalid -c commit.gpgsign=false commit -qm reviewed
SHA=$(git -C "$REPO" rev-parse HEAD)
printf 'SECRET_SENTINEL\n' > "$REPO/.env"
printf 'SECRET_SENTINEL\n' > "$REPO/untracked.txt"
printf 'unreviewed SECRET_SENTINEL\n' > "$REPO/tracked.txt"

PASSED='  Passed Fleet.Temporal.Tests.Services.WorkflowFailureNotifierRealBrokerTests.RealAmqp_UnreachableFirstAttemptThenDelivery_RestartSendsNothing_NoSecrets [41 s]'
SKIPPED='  Skipped Fleet.Temporal.Tests.Services.WorkflowFailureNotifierRealBrokerTests.RealAmqp_UnreachableFirstAttemptThenDelivery_RestartSendsNothing_NoSecrets [1 ms]'

# case <name> <expected exit> <expected leftover kinds, e.g. "net rabbit", or -> <arg> [env...]
case_() {
  local name=$1 want=$2 left=$3 arg=$4; shift 4
  export CALLS="$DIR/calls-$name" STATE="$DIR/state-$name" INPUT="$DIR/input-$name.tar"
  local tmp="$DIR/tmp-$name"
  : > "$CALLS"; mkdir -p "$STATE" "$tmp"
  local rc=0
  env PATH="$DIR/bin:$PATH" TMPDIR="$tmp" READY_SECONDS=3 "$@" bash "$REPO/$RECIPE" "$arg" > "$DIR/out-$name" 2>&1 || rc=$?
  [[ $rc -eq $want ]] || { echo "FAIL $name: exit $rc, wanted $want"; cat "$DIR/out-$name"; exit 1; }
  local actual
  actual=$(ls "$STATE" | sed -E 's/^id-phleet488-[0-9]+-[0-9]+-//' | sort | tr '\n' ' ' | sed 's/ $//')
  [[ $left == - ]] && left=""
  [[ $actual == "$left" ]] || { echo "FAIL $name: left [$actual], wanted [$left]"; cat "$CALLS"; exit 1; }
  [[ -z $(ls -A "$tmp") ]] || { echo "FAIL $name: temp files left"; ls -A "$tmp"; exit 1; }
  ! grep -Eq -- ' (-p|--publish)( |=)[0-9[]| (-P|--publish-all)( |$)' "$CALLS" || { echo "FAIL $name: published a host port"; exit 1; }
  ! grep -Eq -- 'type=bind|--volume|(^| )-v [^ ]*:' "$CALLS" || { echo "FAIL $name: bind-mounted a host path"; exit 1; }
  echo "PASS $name"
}

case_ usage 2 - not-a-sha
case_ unknown-commit 2 - 0123456789abcdef0123456789abcdef01234567
case_ network-create-fails 5 - "$SHA" FAKE_NETWORK_RC=5
case_ broker-start-fails 23 - "$SHA" FAKE_BROKER_RC=23
case_ readiness-times-out 70 - "$SHA" FAKE_PING_RC=1
case_ test-fails 42 - "$SHA" FAKE_TEST_RC=42 "FAKE_TEST_OUTPUT=$PASSED"
case_ test-times-out 124 - "$SHA" FAKE_TEST_RC=124
case_ test-skipped 3 - "$SHA" FAKE_TEST_RC=0 "FAKE_TEST_OUTPUT=$SKIPPED"
case_ no-test-matched 3 - "$SHA" FAKE_TEST_RC=0 FAKE_TEST_OUTPUT=
case_ test-passes 0 - "$SHA" FAKE_TEST_RC=0 "FAKE_TEST_OUTPUT=$PASSED"
case_ cleanup-stuck-after-pass 4 "net rabbit" "$SHA" FAKE_STUCK="rabbit net" "FAKE_TEST_OUTPUT=$PASSED"
case_ cleanup-stuck-after-test-failure 42 "net rabbit" "$SHA" FAKE_STUCK="rabbit net" FAKE_TEST_RC=42 "FAKE_TEST_OUTPUT=$PASSED"
case_ cleanup-unverified-after-pass 4 - "$SHA" FAKE_LIST_RC=1 "FAKE_TEST_OUTPUT=$PASSED"
for name in usage unknown-commit; do
  ! grep -q . "$DIR/calls-$name" || { echo "FAIL $name: called docker"; exit 1; }
done
grep -q '^RESULT: PASS$' "$DIR/out-test-passes"
grep -q 'RESULT: FAIL (exit 42)' "$DIR/out-test-fails"
grep -q "^SOURCE: commit $SHA tree " "$DIR/out-test-passes"
grep -Eq '^docker rm -f -v id-phleet488-[0-9]+-[0-9]+-sdk$' "$DIR/calls-test-passes"
for name in cleanup-stuck-after-pass cleanup-stuck-after-test-failure cleanup-unverified-after-pass; do
  grep -q '^CLEANUP: UNRESOLVED ' "$DIR/out-$name" || { echo "FAIL $name: unresolved cleanup not reported"; exit 1; }
  ! grep -q '^RESULT: PASS$' "$DIR/out-$name" || { echo "FAIL $name: reported PASS"; exit 1; }
done
echo 'PASS recipe reports the test status through owned cleanup'

# A recipe that is not the reviewed commit's copy runs nothing.
printf '# unreviewed\n' >> "$REPO/$RECIPE"
case_ recipe-not-reviewed 2 - "$SHA"
! grep -q . "$DIR/calls-recipe-not-reviewed" || { echo "FAIL recipe-not-reviewed: called docker"; exit 1; }
git -C "$REPO" checkout -q -- "$RECIPE"

# The SDK container's input is exactly the reviewed commit's tracked tree.
diff <(tar -tf "$DIR/input-test-passes.tar" | grep -v '/$' | sort) \
     <(git -C "$REPO" ls-tree -r --name-only "$SHA" | sort)
[[ $(tar -xOf "$DIR/input-test-passes.tar" tracked.txt) == reviewed ]]
! tar -xOf "$DIR/input-test-passes.tar" | grep -q SECRET_SENTINEL
echo 'PASS input is the reviewed tree only: no ignored, untracked or uncommitted content'
