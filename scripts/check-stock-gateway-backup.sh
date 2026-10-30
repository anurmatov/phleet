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
# #502 restore eligibility (no restore is performed). Refused unless: the snapshot is COMPLETE with a
# non-empty SHA256SUMS inventory listing exactly every file, all hashes match, the gateway DB passes
# integrity_check and the required DB/config/trust/control files are present; the current control inputs
# are regular, owned by this uid and not group/other accessible; the current source digest, identity and
# the complete binding row (bindingSha256, every owner/epoch/workspace/provider tuple, state and retired
# references) equal the snapshot's; and the strictly validated current refresh observation was taken after
# the capture and shows no provider refresh at or after it (parsed UTC instants, sub-second kept, never
# string order). A null LAST_REFRESH is accepted only while the snapshot's was also null. No
# token-generation counter is inferred; the control JSON copies are evidence and are never imported.
set -euo pipefail

if [[ "${1:-}" == "--restore-eligibility" ]]; then
  [[ $# -eq 8 && "$3" == "--subscription-source" && "$5" == "--managed-binding" && "$7" == "--refresh-observation" ]] \
    || { echo "usage: $0 --restore-eligibility SNAPSHOT --subscription-source FILE --managed-binding FILE --refresh-observation FILE" >&2; exit 2; }
  python3 - "$2" "$4" "$6" "$8" <<'PY'
import datetime, hashlib, json, os, re, sqlite3, stat, sys
snapshot, source_path, binding_path, refresh_path = sys.argv[1:5]

def refuse(reason):
    sys.exit(f"REFUSED: {reason}")

def regular(path):
    return os.path.lexists(path) and stat.S_ISREG(os.lstat(path).st_mode)

def strict(raw):
    def pairs(items):
        keys = [k for k, _ in items]
        if len(keys) != len(set(keys)):
            raise ValueError("duplicate key")
        return dict(items)
    return json.loads(raw, object_pairs_hook=pairs)

UTC = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z")
def instant(value, what, nullable=False):
    """Strict UTC timestamp to an aware datetime (sub-second kept); null only where explicitly allowed."""
    if value is None and nullable:
        return None
    if not isinstance(value, str) or not UTC.fullmatch(value):
        refuse(f"{what} is missing or not a UTC timestamp")
    base, _, fraction = value[:-1].partition(".")
    parsed = datetime.datetime.strptime(base, "%Y-%m-%dT%H:%M:%S").replace(tzinfo=datetime.timezone.utc)
    return parsed + datetime.timedelta(microseconds=int((fraction + "000000")[:6]) if fraction else 0)

# 1. Complete, exact checksum inventory over the whole snapshot (review B1).
if not os.path.isdir(snapshot) or os.path.islink(snapshot) or not regular(os.path.join(snapshot, "COMPLETE")) \
        or os.path.lexists(os.path.join(snapshot, "INVALID")) or not regular(os.path.join(snapshot, "SHA256SUMS")):
    refuse("snapshot is not complete")
listed = {}
for line in open(os.path.join(snapshot, "SHA256SUMS")).read().splitlines():
    match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
    if not match or match.group(2) in listed or match.group(2).startswith("/") or ".." in match.group(2).split("/"):
        refuse("snapshot checksum inventory is malformed")
    listed[match.group(2)] = match.group(1)
present = set()
for root, dirs, names in os.walk(snapshot):
    for name in dirs + names:
        path = os.path.join(root, name)
        if os.path.islink(path):
            refuse("snapshot contains a link")
        if name in names and os.path.relpath(path, snapshot) not in ("SHA256SUMS", "COMPLETE"):
            present.add(os.path.relpath(path, snapshot))
if not listed or set(listed) != present:
    refuse("snapshot checksum inventory is empty or does not cover every file")
for name, digest in listed.items():
    if hashlib.sha256(open(os.path.join(snapshot, name), "rb").read()).hexdigest() != digest:
        refuse("snapshot hash mismatch")
REQUIRED = ["gateway.db", "state/tls/gateway.pem", "state/tls/gateway-key.pem", "state/tls/ca.pem", "state/tls/client-ca.pem",
            "state/tls/jwt/signing.pem", "state/tls/jwt/public.pem", "state/tls/jwt/kid", "control/subscription.json",
            "control/applied-runtime-binding.json", "control/refresh-observation.json", "control/MANIFEST.json"]
if any(name not in listed for name in REQUIRED) or len([n for n in listed if n.startswith("config/")]) != 1:
    refuse("snapshot lacks required gateway DB/config/trust or control coverage")
try:
    database = sqlite3.connect(f"file:{os.path.join(snapshot, 'gateway.db')}?mode=ro", uri=True)
    healthy = database.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    database.close()
except sqlite3.Error:
    healthy = False
if not healthy:
    refuse("snapshot gateway DB integrity check failed")
try:
    manifest = strict(open(os.path.join(snapshot, "control", "MANIFEST.json"), "rb").read().decode("utf-8"))
    recorded = strict(open(os.path.join(snapshot, "control", "applied-runtime-binding.json"), "rb").read().decode("utf-8"))
except (ValueError, UnicodeDecodeError):
    refuse("snapshot control records unreadable")
if manifest.get("bindingSha256") != listed["control/applied-runtime-binding.json"] \
        or manifest.get("sourceSha256") != listed["control/subscription.json"]:
    refuse("snapshot manifest does not match its control records")

# 2. Current control inputs: regular, non-linked, owned by this uid, no group/other access (review L3, as backup).
for path in (source_path, binding_path, refresh_path):
    if not regular(path):
        refuse("current control input missing or not a regular non-linked file")
    info = os.lstat(path)
    if info.st_uid != os.geteuid() or info.st_mode & 0o077:
        refuse("current control input has a foreign owner or group/other access")
try:
    current_source = open(source_path, "rb").read()
    current_binding = open(binding_path, "rb").read()
    source = strict(current_source.decode("utf-8"))
    binding = strict(current_binding.decode("utf-8"))
    refresh = strict(open(refresh_path, "rb").read().decode("utf-8"))
except (ValueError, UnicodeDecodeError):
    refuse("current control input unreadable")

# 3. Identity, source and the complete binding row (review B2).
identity = ("providerId", "providerWorkspace", "sandboxWorkspace", "ownerGeneration", "gatewayGeneration")
if any(source.get(key) != manifest.get(key) for key in identity):
    refuse("current identity differs from the snapshot")
if hashlib.sha256(current_source).hexdigest() != manifest.get("sourceSha256"):
    refuse("outside source changed since the snapshot")
def authority(record):
    def tuples():
        for slot in ("applied", "candidate"):
            if record.get(slot):
                yield slot, record[slot]
        for item in record.get("superseded") or []:
            yield "superseded", item
    out = set()
    for slot, t in tuples():
        sub = t.get("subscription") or {}
        out.add((slot, t.get("epoch"), t.get("sandboxId"), t.get("sandboxWorkspace"), t.get("authMode"), t.get("provider"),
                 sub.get("providerWorkspace"), sub.get("providerId"), sub.get("accountId"), sub.get("ownerGeneration")))
    retired = {tuple(sorted((k, str(v)) for k, v in r.items())) for r in record.get("retiredSubscriptions") or []}
    return out, retired, record.get("state")
if authority(binding) != authority(recorded):
    refuse("binding owner, epoch, revocation or retirement identities differ from the snapshot")
if hashlib.sha256(current_binding).hexdigest() != manifest.get("bindingSha256"):
    refuse("binding row changed since the snapshot (CAS value differs)")

# 4. Freshness: strict observation fields, parsed UTC instants, never string order (reviews B3, M1, F1).
if set(refresh) != {"providerId", "ownerGeneration", "statusObservedAtUtc", "providerLastRefreshAtUtc", "nextRefreshAtUtc"} \
        or (refresh["providerId"], refresh["ownerGeneration"]) != (manifest.get("providerId"), manifest.get("ownerGeneration")):
    refuse("current refresh freshness unknown for this provider/owner generation")
captured = instant(manifest.get("snapshotCapturedAtUtc"), "snapshot capture time")
observed = instant(refresh["statusObservedAtUtc"], "current refresh observation time")
latest = instant(refresh["providerLastRefreshAtUtc"], "current last refresh", nullable=True)
instant(refresh["nextRefreshAtUtc"], "current next refresh")
recorded_last = instant(manifest.get("providerLastRefreshAtUtc"), "snapshot last refresh", nullable=True)
if observed <= captured:
    refuse("current refresh observation predates the snapshot (freshness unknown)")
if latest is None:
    if recorded_last is not None:
        refuse("refresh evidence regressed")
else:
    if recorded_last is not None and latest < recorded_last:
        refuse("refresh evidence regressed")
    if latest >= captured:
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
