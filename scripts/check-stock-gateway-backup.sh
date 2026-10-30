#!/usr/bin/env bash
# Stock OpenShell gateway backup staleness check (#492 §16). upgrade.sh runs it when the managed
# runtime is enabled; operators can run it from monitoring too.
#
#   check-stock-gateway-backup.sh BACKUP_DIR [MAX_AGE_HOURS]   # MAX_AGE_HOURS defaults to 26
#
# Reads BACKUP_DIR/last-success written by backup-stock-gateway.sh. Exits nonzero when it is missing,
# malformed, in the future, older than MAX_AGE_HOURS, or not newer than BACKUP_DIR/last-failure, or when
# no complete gateway-* snapshot (COMPLETE and SHA256SUMS, not INVALID) exists: .partial-* dirs and
# incomplete snapshots left by a killed run never count. A snapshot with subscription control records
# (#502) is complete only with its control/MANIFEST.json.
#
#   check-stock-gateway-backup.sh --restore-eligibility SNAPSHOT \
#     --subscription-source FILE --managed-binding FILE --refresh-observation FILE
#
# #502 restore eligibility (no restore is performed): the snapshot must be COMPLETE with matching
# SHA256SUMS and a control manifest, and must NOT be superseded. It is refused when its capture time is
# earlier than the latest independently retained providerLastRefreshAtUtc for that provider UUID/owner
# generation, when the current source digest, identity, native epochs, state or retirement history has
# advanced past the manifest, or when current freshness is unknown. A null LAST_REFRESH is accepted only
# while the latest observation is also null (no successful refresh since). No token-generation counter is
# inferred; the control JSON copies are evidence and are never imported over live state.
set -euo pipefail

if [[ "${1:-}" == "--restore-eligibility" ]]; then
  [[ $# -eq 8 && "$3" == "--subscription-source" && "$5" == "--managed-binding" && "$7" == "--refresh-observation" ]] \
    || { echo "usage: $0 --restore-eligibility SNAPSHOT --subscription-source FILE --managed-binding FILE --refresh-observation FILE" >&2; exit 2; }
  python3 - "$2" "$4" "$6" "$8" <<'PY'
import hashlib, json, os, stat, sys
snapshot, source_path, binding_path, refresh_path = sys.argv[1:5]

def refuse(reason):
    sys.exit(f"REFUSED: {reason}")

def regular(path):
    return os.path.lexists(path) and stat.S_ISREG(os.lstat(path).st_mode) and not os.path.islink(path)

if not os.path.isdir(snapshot) or os.path.islink(snapshot) or not regular(os.path.join(snapshot, "COMPLETE")) \
        or os.path.lexists(os.path.join(snapshot, "INVALID")) or not regular(os.path.join(snapshot, "SHA256SUMS")):
    refuse("snapshot is not complete")
for line in open(os.path.join(snapshot, "SHA256SUMS")).read().splitlines():
    digest, name = line.split("  ", 1)
    path = os.path.join(snapshot, name)
    if not regular(path) or hashlib.sha256(open(path, "rb").read()).hexdigest() != digest:
        refuse("snapshot hash mismatch")
manifest_path = os.path.join(snapshot, "control", "MANIFEST.json")
if not regular(manifest_path):
    refuse("snapshot has no subscription control manifest")
manifest = json.load(open(manifest_path))
for path in (source_path, binding_path, refresh_path):
    if not regular(path):
        refuse("current control input missing or not a regular file")
try:
    current_source = open(source_path, "rb").read()
    source = json.loads(current_source)
    binding = json.loads(open(binding_path, "rb").read())
    refresh = json.load(open(refresh_path))
except (ValueError, UnicodeDecodeError):
    refuse("current control input unreadable")
identity = ("providerId", "providerWorkspace", "sandboxWorkspace", "ownerGeneration", "gatewayGeneration")
if any(source.get(key) != manifest.get(key) for key in identity):
    refuse("current identity differs from the snapshot")
if hashlib.sha256(current_source).hexdigest() != manifest.get("sourceSha256"):
    refuse("outside source changed since the snapshot")
epoch = lambda b: (binding.get(b) or {}).get("epoch")
if (epoch("applied"), epoch("candidate"), binding.get("state"), len(binding.get("retiredSubscriptions") or [])) != (
        manifest.get("nativeAppliedEpoch"), manifest.get("nativeCandidateEpoch"), manifest.get("bindingState"), manifest.get("retiredSubscriptions")):
    refuse("epoch, revocation or retirement state advanced since the snapshot")
if (refresh.get("providerId"), refresh.get("ownerGeneration")) != (manifest.get("providerId"), manifest.get("ownerGeneration")) \
        or "providerLastRefreshAtUtc" not in refresh:
    refuse("current refresh freshness unknown for this provider/owner generation")
latest, captured = refresh["providerLastRefreshAtUtc"], manifest.get("snapshotCapturedAtUtc")
if latest is None:
    if manifest.get("providerLastRefreshAtUtc") is not None:
        refuse("refresh evidence regressed")
elif not captured or captured < latest:
    refuse("snapshot captured before the latest provider refresh (superseded)")
print("ELIGIBLE: snapshot is the latest stopped checkpoint for this tuple; restore only gateway DB/KEK/config/trust while stopped")
PY
  exit $?
fi

[[ $# -ge 1 && $# -le 2 ]] || { echo "usage: $0 BACKUP_DIR [MAX_AGE_HOURS]" >&2; exit 2; }
BACKUP_DIR=$1
MAX_AGE_HOURS=${2:-26}
[[ "$MAX_AGE_HOURS" =~ ^[1-9][0-9]*$ ]] || { echo "usage: MAX_AGE_HOURS must be a positive integer" >&2; exit 2; }

python3 - "$BACKUP_DIR" "$MAX_AGE_HOURS" <<'PY'
import datetime, os, sys
backup_dir, max_hours = sys.argv[1], int(sys.argv[2])

def stamp(name):
    path = os.path.join(backup_dir, name)
    if not os.path.isfile(path):
        return None
    with open(path) as handle:
        text = handle.read().strip()
    try:
        return datetime.datetime.strptime(text, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
    except ValueError:
        sys.exit(f"STALE: {name} in {backup_dir} is malformed")

success, failure = stamp("last-success"), stamp("last-failure")
now = datetime.datetime.now(datetime.timezone.utc)
if success is None:
    sys.exit(f"STALE: no successful gateway backup recorded in {backup_dir}")
if success > now + datetime.timedelta(minutes=5):
    sys.exit("STALE: last-success is in the future")
age = now - success
if age > datetime.timedelta(hours=max_hours):
    sys.exit(f"STALE: last gateway backup is {int(age.total_seconds() // 3600)}h old (limit {max_hours}h)")
if failure is not None and failure >= success:
    sys.exit("STALE: the latest gateway backup run failed (last-failure is not older than last-success)")
def complete(name):
    path = os.path.join(backup_dir, name)
    control = os.path.join(path, "control")
    return (name.startswith("gateway-") and os.path.isdir(path) and not os.path.islink(path)
            and os.path.isfile(os.path.join(path, "COMPLETE")) and os.path.isfile(os.path.join(path, "SHA256SUMS"))
            and not os.path.lexists(os.path.join(path, "INVALID"))
            and (not os.path.lexists(control) or os.path.isfile(os.path.join(control, "MANIFEST.json"))))
if not any(complete(name) for name in os.listdir(backup_dir)):
    sys.exit(f"STALE: no complete gateway snapshot in {backup_dir}")
print(f"OK: last gateway backup {int(age.total_seconds() // 60)}m ago")
PY
