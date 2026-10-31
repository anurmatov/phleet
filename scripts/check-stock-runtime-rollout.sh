#!/usr/bin/env bash
# Managed stock OpenShell rollout guard (#492, #502, #507). Run before ANY path that replaces the orchestrator:
# upgrade.sh calls it, and it is the documented operator rollback entrypoint.
#
#   check-stock-runtime-rollout.sh --dockerfile PATH   # source about to be built (upgrade.sh)
#   check-stock-runtime-rollout.sh --image IMAGE       # an image about to be run (rollback)
#
# A target declaring LABEL org.phleet.managed-stock-runtime=1, LABEL org.phleet.managed-stock-subscription=1 and
# LABEL org.phleet.managed-stock-workspace=1 understands managed, subscription and #507 workspace-layout bindings
# and passes. Any other target needs the running orchestrator's operator-authenticated rollback preflight, whose
# answer must have exactly the keys desiredOpenShell, appliedOpenShell, unresolvedBindings and
# subscriptionBindings (nonnegative integers). A target without the managed label requires all four counts zero.
# A managed target without the workspace label would apply its old /workspace layout to a binding whose user tree
# now lives under HOME, so it requires appliedOpenShell=0 and unresolvedBindings=0 (every agent handed back to
# Docker through this checkout first), plus subscriptionBindings=0 when it also lacks the subscription label. A
# nonzero, malformed or unavailable answer refuses; it is never treated as "none".
#
# Run THIS (workspace-capable) guard against the intended target BEFORE switching checkout or replacing guard
# files: an older checkout's guard/upgrade.sh takes the two-label shortcut for a pre-#507 managed target and is
# unprotected while any OpenShell binding is applied (and its #502 predecessor while subscriptionBindings>0).
# Environment: ORCHESTRATOR_AUTH_TOKEN, optional FLEET_ORCHESTRATOR_URL (default http://127.0.0.1:3600).
set -euo pipefail

MANAGED_LABEL=org.phleet.managed-stock-runtime
SUBSCRIPTION_LABEL=org.phleet.managed-stock-subscription
WORKSPACE_LABEL=org.phleet.managed-stock-workspace
refuse() { echo "REFUSED: $*" >&2; exit 1; }
usage() { echo "usage: $0 --dockerfile PATH | --image IMAGE" >&2; exit 2; }

[[ $# -eq 2 ]] || usage
case "$1" in
  --dockerfile)
    [[ -f "$2" ]] || refuse "target Dockerfile not found"
    has_label() { grep -qE "^LABEL ${1//./\\.}=1\$" "$2"; } ;;
  --image)
    docker image inspect "$2" >/dev/null 2>&1 || refuse "target image not inspectable"
    has_label() {
      local value
      value=$(docker image inspect --format "{{ index .Config.Labels \"$1\" }}" "$2" 2>/dev/null) || return 1
      [[ "$value" == "1" ]]
    } ;;
  *) usage ;;
esac

managed=false; subscription=false; workspace=false
has_label "$MANAGED_LABEL" "$2" && managed=true
has_label "$SUBSCRIPTION_LABEL" "$2" && subscription=true
has_label "$WORKSPACE_LABEL" "$2" && workspace=true
if $managed && $subscription && $workspace; then
  echo "OK: target supports managed bindings, including subscription and workspace-layout bindings"; exit 0
fi

token=${ORCHESTRATOR_AUTH_TOKEN:-}
[[ -n "$token" ]] || refuse "target lacks a managed capability and no operator token is configured for the preflight"
url=${FLEET_ORCHESTRATOR_URL:-http://127.0.0.1:3600}
# The header goes through curl's stdin config (-K -), never argv, so the token is not visible in ps.
esc=${token//\\/\\\\}; esc=${esc//\"/\\\"}
body=$(printf 'header = "Authorization: Bearer %s"\n' "$esc" \
  | curl -fsS --max-time 10 -K - "$url/api/managed-runtime/rollback-preflight") \
  || refuse "rollback preflight unavailable"
python3 - "$body" "$managed" "$subscription" <<'PY' || refuse "managed, subscription or workspace-layout bindings exist for this target, or the preflight answer is invalid"
import json, sys
data = json.loads(sys.argv[1])
keys = {"desiredOpenShell", "appliedOpenShell", "unresolvedBindings", "subscriptionBindings"}
if not isinstance(data, dict) or set(data) != keys or any(type(data[k]) is not int or data[k] < 0 for k in keys):
    sys.exit(1)
if sys.argv[2] != "true":
    required = keys
else:
    required = {"appliedOpenShell", "unresolvedBindings"} | (set() if sys.argv[3] == "true" else {"subscriptionBindings"})
if any(data[k] for k in required):
    print("managed counts: " + ", ".join(f"{k}={data[k]}" for k in sorted(keys)), file=sys.stderr)
    sys.exit(1)
PY
if $managed; then echo "OK: no applied OpenShell, unresolved or (unsupported) subscription bindings remain for this older managed target"
else echo "OK: no managed, unresolved or subscription bindings"; fi
