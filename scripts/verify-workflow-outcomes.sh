#!/usr/bin/env bash
# Workflow failure accountability (#476): operator bootstrap and qualification helper.
#
#   bash scripts/verify-workflow-outcomes.sh plan --namespace <ns>
#   bash scripts/verify-workflow-outcomes.sh verify --manifest <file> --approval <https-url>
#
# plan    Strictly read-only. Prints the enrollment/provenance checklist and, when
#         WORKFLOW_OUTCOMES_ORCHESTRATOR_URL and an operator token are provided, GETs
#         /api/workflow-outcomes/coverage and /health and prints the coverage gaps for <ns>.
#         It never POSTs, never starts a workflow and never migrates anything.
#         Exit: 0 coverage read and no gap, 1 coverage read with gaps, 2 invalid usage,
#         3 BLOCKED (coverage not readable: only the static checklist was printed).
# verify  Later, separately approved host qualification (AC25). Refuses (exit 3) unless the
#         approval is an https URL equal to the manifest's approvalRef and the manifest declares
#         throwaway namespaces, a coordinator clone, bounded resource names and time, a throwaway
#         database server and cleanup. Records exact source/image/config hashes, then runs
#         tests/integration/workflow-outcomes/run.sh all --manifest <file> within the time bound.
#         It never starts workflows, migrates a live database or touches production names itself.
#         Exit: run.sh's code (0 PASS, 1 FAIL, 2 INVALID, 3 BLOCKED).
#
# Environment (plan):
#   WORKFLOW_OUTCOMES_ORCHESTRATOR_URL         orchestrator origin, e.g. http://127.0.0.1:3600
#   WORKFLOW_OUTCOMES_OPERATOR_TOKEN_FILE      file holding the operator (admin) bearer token, mode 0600
#   WORKFLOW_OUTCOMES_OPERATOR_TOKEN           alternative to the file (avoid: shells keep history)
#   FLEET_WORKFLOW_OUTCOMES_FULL_SCAN_SECONDS  used for the retention-margin check (default 300)
#   WORKFLOW_OUTCOMES_POLICY_DIR               optional local policy dir to hash (default ./fleet/workflow-outcomes)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNNER="$ROOT/tests/integration/workflow-outcomes/run.sh"

usage() { sed -n '2,5p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }
refuse() { echo "REFUSED: $*" >&2; exit 3; }

sha256_file() { # GNU coreutils or BSD/macOS
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

valid_namespace() { printf '%s' "$1" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]{0,254}$'; }

# ── plan ─────────────────────────────────────────────────────────────────────
static_checklist() {
  local ns="$1" dir="${WORKFLOW_OUTCOMES_POLICY_DIR:-$ROOT/fleet/workflow-outcomes}" f
  cat <<EOF
Workflow failure accountability — enrollment plan for namespace '$ns' (read-only)

Source and provenance (operator-verified; this script cannot prove any of them):
  [ ] reviewed source commit:      $(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)
  [ ] working tree clean:          $([ -z "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ] && echo yes || echo NO)
  [ ] selected image digests recorded for fleet:orchestrator, fleet:agent, fleet:temporal-bridge
      (docker image inspect --format '{{.Id}}' <image>) and the previous digests kept for rollback
  [ ] namespace '$ns' retention and archival read from Temporal (DescribeNamespace) and recorded
  [ ] FULL_SCAN_SECONDS leaves at least two full sweeps inside the retention
  [ ] every Completed-business classifier rule pinned to the loaded definition hash of '$ns'
  [ ] ownership.json maps '$ns' (repoProjects / namespaceProjects) to active project names
  [ ] fleet-mysql dump and workflow_outcomes_keys volume backup taken and a restore rehearsed
  [ ] coordinator scoped token provisioned out of band; never in chat, logs or an agent Env Ref
  [ ] feature stays FLEET_WORKFLOW_OUTCOMES_ENABLED=false until the enrollment approval

Local policy files ($dir):
EOF
  for f in ownership.json classifiers.json; do
    if [ -r "$dir/$f" ]; then echo "  $f sha256 $(sha256_file "$dir/$f")"
    else echo "  $f not readable here — provenance gap: hash not recorded (set WORKFLOW_OUTCOMES_POLICY_DIR)"; fi
  done
}

plan() {
  local ns="" url token="" body_cov body_health rc health_rc
  while [ $# -gt 0 ]; do
    case "$1" in
      --namespace) [ $# -ge 2 ] || usage; ns="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  [ -n "$ns" ] || usage
  valid_namespace "$ns" || { echo "INVALID: namespace must match [A-Za-z0-9][A-Za-z0-9._-]*" >&2; exit 2; }
  static_checklist "$ns"

  url="${WORKFLOW_OUTCOMES_ORCHESTRATOR_URL:-}"
  if [ -n "${WORKFLOW_OUTCOMES_OPERATOR_TOKEN_FILE:-}" ]; then
    [ -r "$WORKFLOW_OUTCOMES_OPERATOR_TOKEN_FILE" ] || { echo "INVALID: token file not readable" >&2; exit 2; }
    token="$(tr -d '\r\n' < "$WORKFLOW_OUTCOMES_OPERATOR_TOKEN_FILE")"
  else
    token="${WORKFLOW_OUTCOMES_OPERATOR_TOKEN:-}"
  fi
  if [ -z "$url" ] || [ -z "$token" ]; then
    echo
    echo "BLOCKED: live coverage not read (set WORKFLOW_OUTCOMES_ORCHESTRATOR_URL and WORKFLOW_OUTCOMES_OPERATOR_TOKEN_FILE)."
    exit 3
  fi
  printf '%s' "$url" | grep -Eq '^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?/?$' \
    || { echo "INVALID: WORKFLOW_OUTCOMES_ORCHESTRATOR_URL must be an http(s) origin without userinfo, path or query" >&2; exit 2; }
  command -v curl >/dev/null 2>&1 || { echo "BLOCKED: curl is required for the live read" >&2; exit 3; }
  url="${url%/}"

  # GET only, no redirects, bounded. The bearer goes through curl's config on stdin so it never
  # appears in the process list.
  get() {
    printf 'header = "Authorization: Bearer %s"\n' "$token" \
      | curl --config - -fsS --max-time 10 --max-redirs 0 --proto '=http,https' "$url$1"
  }
  set +e
  body_cov="$(get /api/workflow-outcomes/coverage)"; rc=$?
  body_health="$(get /api/workflow-outcomes/health)"; health_rc=$?
  set -e
  if [ "$rc" -ne 0 ] || [ -z "$body_cov" ]; then
    echo
    echo "BLOCKED: GET /api/workflow-outcomes/coverage failed (curl exit $rc). 503 = feature disabled or"
    echo "auth not configured, 401 = wrong operator token. Nothing was changed."
    exit 3
  fi
  echo
  COVERAGE="$body_cov" HEALTH="$body_health" HEALTH_RC="$health_rc" NS="$ns" FULL="${FLEET_WORKFLOW_OUTCOMES_FULL_SCAN_SECONDS:-300}" python3 - <<'PY'
import json, os, sys
from datetime import datetime, timezone
ns, full = os.environ['NS'], int(os.environ['FULL'])
try:
    cov = json.loads(os.environ['COVERAGE'])
except ValueError:
    print('BLOCKED: coverage reply is not JSON'); sys.exit(3)
gaps = []
# Health that could not be read is unknown, never clear.
health = {}
if os.environ.get('HEALTH_RC', '1') != '0' or not (os.environ.get('HEALTH') or '').strip():
    gaps.append(f"health could not be read (curl exit {os.environ.get('HEALTH_RC')}): not clear")
else:
    # Only an object with an explicit state of exactly "healthy" is clear; {}, [], null and any
    # other shape or state are not.
    try:
        parsed = json.loads(os.environ['HEALTH'])
    except ValueError:
        gaps.append('health reply is not JSON: not clear')
    else:
        if not isinstance(parsed, dict):
            gaps.append('health reply is not an object: not clear')
        elif parsed.get('state') != 'healthy':
            gaps.append(f"health state is {parsed.get('state')!r}: not clear")
        else:
            health = parsed
if not cov.get('enabled'):
    gaps.append('feature disabled: no coverage is claimed')
if cov.get('degraded'):
    gaps.append('observer reports degraded coverage')
row = next((n for n in cov.get('namespaces') or [] if n.get('namespace') == ns), None)
if row is None:
    gaps.append(f"namespace '{ns}' is not in FLEET_WORKFLOW_OUTCOMES_NAMESPACES / not enrolled")
else:
    print(f"namespace {ns}: state={row.get('state')} retentionSeconds={row.get('retentionSeconds')} "
          f"enrolledAt={row.get('enrolledAt')} lastRecentPassAt={row.get('lastRecentPassAt')} "
          f"lastFullPassAt={row.get('lastFullPassAt')} knownPending={row.get('knownPending')}")
    if row.get('state') != 'healthy':
        gaps.append(f"namespace state is {row.get('state')}")
    retention = row.get('retentionSeconds')
    if isinstance(retention, (int, float)) and retention < 3 * full:
        gaps.append(f'retention margin below two full-scan intervals (retention {retention}s, full scan {full}s)')
    if not row.get('enrolledAt'):
        gaps.append('no enrollment record: catch-up has not started')
    if not row.get('lastFullPassAt'):
        gaps.append('no completed full retention sweep yet')
    else:
        try:
            last = datetime.fromisoformat(str(row['lastFullPassAt']).replace('Z', '+00:00'))
            age = (datetime.now(timezone.utc) - last).total_seconds()
            if age > 2 * full:
                gaps.append(f'last full sweep is {int(age)}s old (> 2 x {full}s)')
        except ValueError:
            gaps.append('lastFullPassAt is not a timestamp')
    for gap in row.get('gaps') or []:
        gaps.append(f"coverage gap {gap.get('start')}..{gap.get('end')} reason={gap.get('reason')} "
                    f"unknownRunCount={gap.get('unknownRunCount')}")
for incident in health.get('incidents') or []:
    if incident.get('namespace') in (None, '', ns) and incident.get('state') != 'resolved':
        gaps.append(f"health incident {incident.get('component')}/{incident.get('reason')} since {incident.get('firstSeenAt')}")
if gaps:
    print('Coverage gaps:')
    for gap in gaps:
        print('  - ' + gap)
    sys.exit(1)
print('No coverage gap reported for this namespace. Provenance items above remain operator checks.')
PY
}

# ── verify ───────────────────────────────────────────────────────────────────
verify() {
  local manifest="" approval="" out rc bound
  while [ $# -gt 0 ]; do
    case "$1" in
      --manifest) [ $# -ge 2 ] || usage; manifest="$2"; shift 2 ;;
      --approval) [ $# -ge 2 ] || usage; approval="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  [ -n "$approval" ] || refuse "an --approval https URL for this exact qualification is required"
  printf '%s' "$approval" | grep -Eq '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/[^[:space:]]*$' \
    || refuse "--approval must be an https URL (no userinfo)"
  [ -n "$manifest" ] && [ -f "$manifest" ] || refuse "--manifest must name an existing file"
  command -v python3 >/dev/null 2>&1 || refuse "python3 is required"

  out="$(mktemp -d "${TMPDIR:-/tmp}/wo-qualification.XXXXXX")"
  # Validation and provenance capture; prints the run bound in seconds on success.
  set +e
  bound="$(MANIFEST="$manifest" APPROVAL="$approval" OUT="$out" ROOT="$ROOT" python3 - <<'PY'
import hashlib, json, os, re, subprocess, sys
from urllib.parse import urlsplit

def refuse(msg):
    print('REFUSED: ' + msg, file=sys.stderr); sys.exit(3)

def pairs(items):
    out = {}
    for k, v in items:
        if k in out: refuse(f'duplicate key {k!r} in manifest')
        out[k] = v
    return out

path, approval, out, root = os.environ['MANIFEST'], os.environ['APPROVAL'], os.environ['OUT'], os.environ['ROOT']
raw = open(path, 'rb').read()
try:
    m = json.loads(raw, object_pairs_hook=pairs)
except ValueError:
    refuse('manifest is not valid JSON')
if not isinstance(m, dict) or m.get('schemaVersion') != 1 or m.get('kind') != 'workflow-outcomes-qualification':
    refuse('manifest needs schemaVersion 1 and kind "workflow-outcomes-qualification"')
if m.get('approvalRef') != approval:
    refuse('manifest approvalRef must equal --approval exactly')
if urlsplit(approval).username or urlsplit(approval).password:
    refuse('approval URL must not carry credentials')

prefix = m.get('resourcePrefix')
if not isinstance(prefix, str) or not re.fullmatch(r'[a-z][a-z0-9]{2,20}-[a-z0-9]{2,20}-', prefix):
    refuse('resourcePrefix must look like "<word>-<runid>-" (lowercase, bounded)')
# Production names this helper never accepts, plus the live configuration if it is visible here.
production = {'fleet', 'default', 'temporal-system', 'fleet-mysql', 'fleet-orchestrator', 'fleet-temporal-bridge',
              'fleet-bridge', 'fleet-dashboard', 'rabbitmq', 'temporal-server'}
production |= {n.strip() for n in os.environ.get('FLEET_WORKFLOW_OUTCOMES_NAMESPACES', '').split(',') if n.strip()}
if os.environ.get('FLEET_CTO_AGENT'):
    production.add(os.environ['FLEET_CTO_AGENT'])
if prefix.rstrip('-') in production or prefix.startswith('fleet-'):
    refuse('resourcePrefix collides with a production name')

def owned(value, what):
    if not isinstance(value, str) or not value.startswith(prefix) or len(value) > 63 \
            or not re.fullmatch(r'[a-z0-9][a-z0-9._-]*', value) or value in production:
        refuse(f'{what} {value!r} must be a bounded lowercase name starting with {prefix!r}')
    return value

namespaces = m.get('namespaces')
if not isinstance(namespaces, list) or not namespaces or len(namespaces) > 10:
    refuse('namespaces must list 1..10 throwaway namespaces')
for n in namespaces: owned(n, 'namespace')
for q in m.get('taskQueues', []): owned(q, 'task queue')
clone = m.get('coordinatorClone')
if not isinstance(clone, dict) or not isinstance(clone.get('cloneOf'), str) or not clone['cloneOf']:
    refuse('coordinatorClone {name, cloneOf} is required')
owned(clone.get('name'), 'coordinatorClone.name')
db = m.get('database')
if not isinstance(db, dict) or db.get('connectionEnv') != 'FLEET_ORCHESTRATOR_MIGRATION_CONNECTION':
    refuse('database.connectionEnv must be FLEET_ORCHESTRATOR_MIGRATION_CONNECTION')
owned(db.get('server'), 'database.server')
conn = os.environ.get('FLEET_ORCHESTRATOR_MIGRATION_CONNECTION', '')
server = re.search(r'(?:^|;)\s*(?:server|host|data source)\s*=\s*([^;]+)', conn, re.I)
if not server or server.group(1).strip().lower() != db['server']:
    refuse('FLEET_ORCHESTRATOR_MIGRATION_CONNECTION must point at database.server (the throwaway server)')
limit = m.get('maxDurationSeconds')
if not isinstance(limit, int) or isinstance(limit, bool) or not 60 <= limit <= 21600:
    refuse('maxDurationSeconds must be an integer 60..21600')
cleanup = m.get('cleanup')
if not isinstance(cleanup, dict) or cleanup.get('verify') is not True or not isinstance(cleanup.get('owned'), list) \
        or not cleanup['owned']:
    refuse('cleanup {owned: [...], verify: true} is required')
for name in cleanup['owned']: owned(name, 'cleanup entry')
listed = set(cleanup['owned'])
for name in namespaces + [clone['name'], db['server']] + list(m.get('taskQueues', [])):
    if name not in listed: refuse(f'{name!r} is created but not listed in cleanup.owned')

def run(*argv):
    try:
        r = subprocess.run(argv, capture_output=True, text=True)
    except OSError:
        return None
    return r.stdout.strip() if r.returncode == 0 else None

head = run('git', '-C', root, 'rev-parse', '--verify', 'HEAD^{commit}')
if head is None or not re.fullmatch(r'[0-9a-f]{40}', head):
    refuse('Git HEAD read failed or did not return an exact commit')
status = run('git', '-C', root, 'status', '--porcelain')
if status is None:
    refuse('Git status read failed: clean provenance is unknown')
dirty = bool(status)

refs = m.get('images')
if not isinstance(refs, dict) or not {'orchestrator', 'agent', 'temporalBridge'} <= refs.keys():
    refuse('images {orchestrator, agent, temporalBridge: <ref>} are required')
images = {}
for role, ref in refs.items():
    if not isinstance(ref, str) or not ref.strip():
        refuse('every image role needs a nonempty reference')
    image_id = run('docker', 'image', 'inspect', '--format', '{{.Id}}', ref)
    if not image_id:
        print(f'BLOCKED: image {ref!r} for {role} cannot be inspected; exact image hashes are required', file=sys.stderr)
        sys.exit(3)
    images[role] = {'ref': ref, 'id': image_id}
files = m.get('configFiles')
if not isinstance(files, list) or not all(isinstance(f, str) and f for f in files) \
        or not {'ownership.json', 'classifiers.json'} <= {os.path.basename(f) for f in files}:
    refuse('configFiles must include readable ownership.json and classifiers.json policies')
configs = {}
for f in files:
    p = f if os.path.isabs(f) else os.path.join(os.path.dirname(os.path.abspath(path)), f)
    if not os.path.isfile(p):
        refuse(f'config file {f!r} not found')
    try:
        with open(p, 'rb') as stream:
            configs[f] = hashlib.sha256(stream.read()).hexdigest()
    except OSError:
        refuse(f'config file {f!r} is not readable')
provenance = {'receipt': 'workflow-outcomes-qualification-provenance', 'approval': approval,
              'manifestSha256': hashlib.sha256(raw).hexdigest(), 'head': head, 'dirty': dirty,
              'images': images, 'configFiles': configs, 'resourcePrefix': prefix,
              'namespaces': namespaces, 'maxDurationSeconds': limit}
with open(os.path.join(out, 'provenance.json'), 'w') as f:
    json.dump(provenance, f, indent=2, sort_keys=True)
if dirty:
    refuse('working tree is not clean: qualification needs the exact reviewed commit')
print(limit)
PY
)"
  rc=$?
  set -e
  [ "$rc" -eq 0 ] || exit "$rc"
  echo "Provenance: $out/provenance.json" >&2

  # Bounded run without GNU timeout(1), so it also works on BSD/macOS. Job control puts the runner
  # in its own process group, so the bound stops its dotnet/npm children too: bash defers a TERM
  # until its foreground child exits, so killing the runner alone would not stop the run.
  set -m
  bash "$RUNNER" all --manifest "$manifest" --results-dir "$out/results" &
  local pid=$! waited=0
  set +m
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$waited" -ge "$bound" ]; then
      kill -TERM -- "-$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
      sleep 2
      kill -KILL -- "-$pid" 2>/dev/null || true
      echo "FAIL: qualification exceeded maxDurationSeconds=$bound; run cleanup for the owned names" >&2
      exit 1
    fi
    sleep 5; waited=$((waited + 5))
  done
  set +e
  wait "$pid"; rc=$?
  set -e
  echo "Receipt: $out/results/receipt.json (rc=$rc). Cleanup of cleanup.owned is the operator's next step." >&2
  exit "$rc"
}

[ $# -ge 1 ] || usage
cmd="$1"; shift
case "$cmd" in
  plan) plan "$@" ;;
  verify) verify "$@" ;;
  *) usage ;;
esac
