#!/usr/bin/env bash
# Daily protected backup of the stock OpenShell gateway state (#492 §16). Run from the operator's
# daily host backup job and before any gateway configuration change or upgrade.
#
#   backup-stock-gateway.sh BACKUP_DIR [STATE_DIR]     # STATE_DIR defaults to /var/lib/openshell
#
# SQLite is copied with the stdlib online backup API (never a raw copy of an open DB/WAL) and
# integrity-checked. Config and trust files are copied as-is. Their inode/mtime is compared before
# and after; any change, or any failure, leaves the snapshot marked INVALID and exits nonzero so the
# operator alert fires. Directories are 0700 and files 0600; seven complete snapshots are kept.
# Snapshots hold key material: keep them host-local unless an operator-approved encrypted transfer
# exists. Never upload them to a public share or an unencrypted sync.
set -euo pipefail
umask 077

[[ $# -ge 1 && $# -le 2 ]] || { echo "usage: $0 BACKUP_DIR [STATE_DIR]" >&2; exit 2; }
BACKUP_DIR=$1
STATE_DIR=${2:-/var/lib/openshell}
KEEP=7
[[ -f "$STATE_DIR/gateway.db" ]] || { echo "backup_failed: gateway.db not found" >&2; exit 1; }

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
snapshot="$BACKUP_DIR/gateway-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir "$snapshot"

python3 - "$STATE_DIR" "$snapshot" <<'PY' || { : > "$snapshot/INVALID"; echo "backup_failed: snapshot marked INVALID" >&2; exit 1; }
import hashlib, os, shutil, sqlite3, stat, sys
state, out = sys.argv[1], sys.argv[2]

def trust_files():
    files = []
    for root, dirs, names in os.walk(state):
        dirs.sort()
        for name in sorted(names):
            if name.startswith("gateway.db"):
                continue  # the database is copied through the online backup API below
            path = os.path.join(root, name)
            if not stat.S_ISREG(os.lstat(path).st_mode):
                continue  # symlinks and special files are not followed
            files.append(path)
    return files

def fingerprint(paths):
    return {p: (os.lstat(p).st_ino, os.lstat(p).st_mtime_ns) for p in paths}

before = fingerprint(trust_files())
source = sqlite3.connect(f"file:{os.path.join(state, 'gateway.db')}?mode=ro", uri=True)
target = sqlite3.connect(os.path.join(out, "gateway.db"))
with target:
    source.backup(target)
source.close()
if target.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
    sys.exit("integrity_check_failed")
target.close()
for path in before:
    dest = os.path.join(out, "state", os.path.relpath(path, state))
    os.makedirs(os.path.dirname(dest), mode=0o700, exist_ok=True)
    shutil.copy2(path, dest)
if fingerprint(trust_files()) != before:
    sys.exit("trust_files_changed_during_backup")
sums = []
for root, _, names in os.walk(out):
    for name in sorted(names):
        path = os.path.join(root, name)
        os.chmod(path, 0o600)
        with open(path, "rb") as handle:
            sums.append(f"{hashlib.sha256(handle.read()).hexdigest()}  {os.path.relpath(path, out)}")
with open(os.path.join(out, "SHA256SUMS"), "w") as handle:
    handle.write("\n".join(sums) + "\n")
os.chmod(os.path.join(out, "SHA256SUMS"), 0o600)
PY

: > "$snapshot/COMPLETE"
date -u +%Y-%m-%dT%H:%M:%SZ > "$BACKUP_DIR/last-success"
# Keep the newest complete snapshots; invalid ones are left for the operator to inspect.
kept=0
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name 'gateway-*' | sort -r | while IFS= read -r dir; do
  [[ -f "$dir/COMPLETE" ]] || continue
  kept=$((kept + 1))
  [[ $kept -le $KEEP ]] || rm -rf -- "$dir"
done
echo "OK: $snapshot"
