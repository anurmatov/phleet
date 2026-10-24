#!/usr/bin/env bash
# Workflow failure accountability (#476) acceptance runner.
#
#   bash tests/integration/workflow-outcomes/run.sh <suite>|all [--manifest <file>] [--results-dir <dir>]
#   bash tests/integration/workflow-outcomes/run.sh --list
#
# Each suite maps (fixtures.json) to the test projects whose tests carry
# [Trait("WorkflowOutcomeSuite", "<suite>")], to the dashboard npm scripts, or to the built-in
# public scrub. Results are TRX files plus one JSON receipt (suite, project, counts, git HEAD,
# rc) printed on stdout and written to <results-dir>/receipt.json.
#
# Exit codes: 0 PASS, 1 FAIL, 2 INVALID, 3 BLOCKED. A suite with zero tests, any failure or any
# skip is FAIL. A missing prerequisite (tool, environment variable, base ref) is BLOCKED, never
# PASS. `all` reports the worst suite: FAIL > INVALID > BLOCKED > PASS.
#
# Local and isolated only: this runs test projects. It never starts workflows, never touches a
# live database (the migration suite creates and drops its own database on the server named by
# FLEET_ORCHESTRATOR_MIGRATION_CONNECTION) and never reads deployment configuration.
#
# Environment:
#   FLEET_ORCHESTRATOR_MIGRATION_CONNECTION  MySQL 8.0 admin connection for the migration suite.
#   WORKFLOW_OUTCOMES_TEST_CONFIGURATION     dotnet configuration (default Debug).
#   WORKFLOW_OUTCOMES_SCRUB_BASE             public-scrub base ref (default origin/main).
#   WORKFLOW_OUTCOMES_SCRUB_DENYLIST         optional operator denylist file (never committed).
#   WORKFLOW_OUTCOMES_SCRUB_ALLOW_REPOS      optional comma list of extra public owner/repo.
#   WORKFLOW_OUTCOMES_SCRUB_ALLOW_BINARY     optional comma list of reviewed binary paths.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../../.." && pwd)"
FIXTURES="$HERE/fixtures.json"
ASSERT="$HERE/assertions.py"
CONFIGURATION="${WORKFLOW_OUTCOMES_TEST_CONFIGURATION:-Debug}"
SCRUB_BASE="${WORKFLOW_OUTCOMES_SCRUB_BASE:-origin/main}"

usage() {
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

SUITE=""
MANIFEST=""
RESULTS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --manifest) [ $# -ge 2 ] || usage; MANIFEST="$2"; shift 2 ;;
    --results-dir) [ $# -ge 2 ] || usage; RESULTS="$2"; shift 2 ;;
    --list) SUITE="--list"; shift ;;
    -h|--help) usage ;;
    -*) echo "INVALID: unknown option $1" >&2; usage ;;
    *) [ -z "$SUITE" ] || usage; SUITE="$1"; shift ;;
  esac
done
[ -n "$SUITE" ] || usage

command -v python3 >/dev/null 2>&1 || { echo "BLOCKED: python3 is required" >&2; exit 3; }
command -v git >/dev/null 2>&1 || { echo "BLOCKED: git is required" >&2; exit 3; }

if [ "$SUITE" = "--list" ]; then
  python3 "$ASSERT" resolve --fixtures "$FIXTURES" --repo "$REPO_ROOT" all | cut -f1,2,3
  exit 0
fi

if [ -n "$MANIFEST" ]; then
  [ -f "$MANIFEST" ] || { echo "INVALID: manifest not found: $MANIFEST" >&2; exit 2; }
  MANIFEST="$(cd "$(dirname "$MANIFEST")" && pwd)/$(basename "$MANIFEST")"
fi

PLAN="$(mktemp "${TMPDIR:-/tmp}/wo-plan.XXXXXX")"
trap 'rm -f "$PLAN"' EXIT
set +e
if [ -n "$MANIFEST" ]; then
  python3 "$ASSERT" resolve --fixtures "$FIXTURES" --manifest "$MANIFEST" --repo "$REPO_ROOT" "$SUITE" > "$PLAN"
else
  python3 "$ASSERT" resolve --fixtures "$FIXTURES" --repo "$REPO_ROOT" "$SUITE" > "$PLAN"
fi
rc=$?
set -e
[ "$rc" -eq 0 ] || exit "$rc"

if [ -z "$RESULTS" ]; then
  RESULTS="$(mktemp -d "${TMPDIR:-/tmp}/wo-results.XXXXXX")"
else
  mkdir -p "$RESULTS"
  RESULTS="$(cd "$RESULTS" && pwd)"
fi
PARTS="$RESULTS/parts.jsonl"
: > "$PARTS"
echo "Results: $RESULTS" >&2

HEAD_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
DIRTY=0
[ -z "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ] || DIRTY=1

# Records one result part; the assertions helper owns every verdict.
record() { python3 "$ASSERT" "$@" >> "$PARTS"; }

missing_env() { # prints the first unset variable of a comma list, or nothing
  local list="$1" name
  [ "$list" = "-" ] && return 0
  local IFS=','
  for name in $list; do
    eval "[ -n \"\${$name:-}\" ]" || { printf '%s' "$name"; return 0; }
  done
}

# Builds each dotnet project at most once per run: "ok", "failed" or "missing".
BUILT=""
build_state() {
  local project="$1" key log
  key="$(printf '%s' "$project" | tr '/.' '__')"
  case " $BUILT " in
    *" $key=ok "*) echo ok; return ;;
    *" $key=failed "*) echo failed; return ;;
  esac
  command -v dotnet >/dev/null 2>&1 || { echo missing; return; }
  log="$RESULTS/build.$key.log"
  if (cd "$REPO_ROOT" && dotnet build "$project" -nologo -v q -c "$CONFIGURATION") > "$log" 2>&1; then
    BUILT="$BUILT $key=ok"; echo ok
  else
    BUILT="$BUILT $key=failed"; echo failed
  fi
}

node_at_least() { # node_at_least 22.18
  local want="$1" have major minor wmajor wminor
  command -v node >/dev/null 2>&1 || return 1
  have="$(node --version 2>/dev/null | sed 's/^v//')"
  major="${have%%.*}"; minor="${have#*.}"; minor="${minor%%.*}"
  wmajor="${want%%.*}"; wminor="${want#*.}"
  [ "$major" -gt "$wmajor" ] || { [ "$major" -eq "$wmajor" ] && [ "$minor" -ge "$wminor" ]; }
}

run_dotnet() {
  local suite="$1" ac="$2" projects="$3" project state name trx rc
  local IFS=','
  for project in $projects; do
    IFS=' '
    state="$(build_state "$project")"
    case "$state" in
      missing) record blocked --suite "$suite" --ac "$ac" --project "$project" --reason "dotnet SDK not on PATH"; continue ;;
      failed)  record error --suite "$suite" --ac "$ac" --project "$project" --reason "build failed, see $RESULTS"; continue ;;
    esac
    name="$(basename "$project" .csproj)"
    trx="$suite.$name.trx"
    echo "== $suite ($ac) $name: WorkflowOutcomeSuite=$suite" >&2
    set +e
    (cd "$REPO_ROOT" && dotnet test "$project" --no-build -nologo -c "$CONFIGURATION" \
      --filter "WorkflowOutcomeSuite=$suite" \
      --logger "trx;LogFileName=$trx" --results-directory "$RESULTS") > "$RESULTS/$suite.$name.log" 2>&1
    rc=$?
    set -e
    tail -n 3 "$RESULTS/$suite.$name.log" >&2 || true
    record trx --suite "$suite" --ac "$ac" --project "$project" --trx "$RESULTS/$trx" --rc "$rc"
  done
}

run_npm() {
  local suite="$1" ac="$2" detail="$3" prefix scripts min script log rc
  prefix="${detail%%|*}"; scripts="${detail#*|}"; min="${scripts#*|}"; scripts="${scripts%%|*}"
  if ! command -v npm >/dev/null 2>&1 || ! node_at_least "$min"; then
    record blocked --kind npm --suite "$suite" --ac "$ac" --project "$prefix" --reason "needs node >= $min and npm on PATH"
    return
  fi
  if [ ! -d "$REPO_ROOT/$prefix/node_modules" ]; then
    record blocked --kind npm --suite "$suite" --ac "$ac" --project "$prefix" --reason "dependencies not installed: run npm --prefix $prefix ci"
    return
  fi
  local IFS=','
  for script in $scripts; do
    IFS=' '
    log="$RESULTS/$suite.npm-$(printf '%s' "$script" | tr ' ' '-').log"
    echo "== $suite ($ac) npm --prefix $prefix $script" >&2
    set +e
    # shellcheck disable=SC2086 # "run build" is two words on purpose.
    (cd "$REPO_ROOT" && npm --prefix "$prefix" $script) > "$log" 2>&1
    rc=$?
    set -e
    tail -n 5 "$log" >&2 || true
    record npm --suite "$suite" --ac "$ac" --script "${script#run }" --log "$log" --rc "$rc"
  done
}

SUITES=""
while IFS="$(printf '\t')" read -r suite ac kind requires detail; do
  [ -n "$suite" ] || continue
  SUITES="$SUITES $suite"
  missing="$(missing_env "$requires")"
  if [ -n "$missing" ]; then
    record blocked --kind "$kind" --suite "$suite" --ac "$ac" --reason "$missing is not set"
    echo "== $suite ($ac): BLOCKED, $missing is not set" >&2
    continue
  fi
  case "$kind" in
    dotnet) run_dotnet "$suite" "$ac" "$detail" ;;
    npm) run_npm "$suite" "$ac" "$detail" ;;
    scrub)
      echo "== $suite ($ac): git diff $SCRUB_BASE...HEAD" >&2
      set +e
      python3 "$ASSERT" scrub --suite "$suite" --ac "$ac" --repo "$REPO_ROOT" --base "$SCRUB_BASE" > "$RESULTS/$suite.json" 2> "$RESULTS/$suite.err"
      rc=$?
      set -e
      if [ "$rc" -eq 0 ]; then
        cat "$RESULTS/$suite.json" >> "$PARTS"
      else
        record invalid --kind scrub --suite "$suite" --ac "$ac" --reason "$(head -n 1 "$RESULTS/$suite.err")"
      fi
      ;;
  esac
done < "$PLAN"

set +e
python3 "$ASSERT" summarize --parts "$PARTS" --suites "$SUITES" --head "$HEAD_SHA" --dirty "$DIRTY" --out "$RESULTS/receipt.json"
rc=$?
set -e
exit "$rc"
