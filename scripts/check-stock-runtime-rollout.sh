#!/usr/bin/env bash
# Managed stock OpenShell rollout guard (#492). Run before ANY path that replaces the orchestrator:
# upgrade.sh calls it, and it is the documented operator rollback entrypoint.
#
#   check-stock-runtime-rollout.sh --dockerfile PATH   # source about to be built (upgrade.sh)
#   check-stock-runtime-rollout.sh --image IMAGE       # an image about to be run (rollback)
#
# A target that declares LABEL org.phleet.managed-stock-runtime=1 understands managed bindings and
# passes. Any other target needs the running orchestrator's operator-authenticated rollback
# preflight to report zero desired OpenShell, applied OpenShell and unresolved bindings. A nonzero,
# malformed or unavailable answer refuses; it is never treated as "none".
# Environment: ORCHESTRATOR_AUTH_TOKEN, optional FLEET_ORCHESTRATOR_URL (default http://127.0.0.1:3600).
set -euo pipefail

LABEL_KEY=org.phleet.managed-stock-runtime
refuse() { echo "REFUSED: $*" >&2; exit 1; }

[[ $# -eq 2 ]] || { echo "usage: $0 --dockerfile PATH | --image IMAGE" >&2; exit 2; }
case "$1" in
  --dockerfile)
    [[ -f "$2" ]] || refuse "target Dockerfile not found"
    if grep -qE "^LABEL ${LABEL_KEY//./\\.}=1\$" "$2"; then echo "OK: target supports managed bindings"; exit 0; fi ;;
  --image)
    label=$(docker image inspect --format "{{ index .Config.Labels \"$LABEL_KEY\" }}" "$2" 2>/dev/null) \
      || refuse "target image not inspectable"
    if [[ "$label" == "1" ]]; then echo "OK: target supports managed bindings"; exit 0; fi ;;
  *) echo "usage: $0 --dockerfile PATH | --image IMAGE" >&2; exit 2 ;;
esac

token=${ORCHESTRATOR_AUTH_TOKEN:-}
[[ -n "$token" ]] || refuse "target lacks managed capability and no operator token is configured for the preflight"
url=${FLEET_ORCHESTRATOR_URL:-http://127.0.0.1:3600}
body=$(curl -fsS --max-time 10 -H "Authorization: Bearer $token" "$url/api/managed-runtime/rollback-preflight") \
  || refuse "rollback preflight unavailable"
python3 - "$body" <<'PY' || refuse "managed bindings exist or the preflight answer is invalid"
import json, sys
data = json.loads(sys.argv[1])
keys = {"desiredOpenShell", "appliedOpenShell", "unresolvedBindings"}
if not isinstance(data, dict) or set(data) != keys or any(type(data[k]) is not int or data[k] < 0 for k in keys):
    sys.exit(1)
if any(data[k] for k in keys):
    print("managed counts: " + ", ".join(f"{k}={data[k]}" for k in sorted(keys)), file=sys.stderr)
    sys.exit(1)
PY
echo "OK: no managed or unresolved bindings"
