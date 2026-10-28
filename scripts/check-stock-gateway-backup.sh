#!/usr/bin/env bash
# Stock OpenShell gateway backup staleness check (#492 §16). upgrade.sh runs it when the managed
# runtime is enabled; operators can run it from monitoring too.
#
#   check-stock-gateway-backup.sh BACKUP_DIR [MAX_AGE_HOURS]   # MAX_AGE_HOURS defaults to 26
#
# Reads BACKUP_DIR/last-success written by backup-stock-gateway.sh. Exits nonzero when it is missing,
# malformed, in the future, older than MAX_AGE_HOURS, or not newer than BACKUP_DIR/last-failure.
set -euo pipefail

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
print(f"OK: last gateway backup {int(age.total_seconds() // 60)}m ago")
PY
