#!/usr/bin/env bash
# Daily protected backup of the stock OpenShell gateway state (#492 §16). Run from the operator's
# daily host backup job and before any gateway configuration change or upgrade.
#
#   backup-stock-gateway.sh BACKUP_DIR CONFIG_FILE [STATE_DIR]   # STATE_DIR defaults to /var/lib/openshell
#
# Coverage is complete or the run fails: gateway.db, the gateway TOML and every trust file it uses
# (TLS server cert/key, client CA, JWT signing/public key and kid under STATE_DIR/tls) must exist.
# SQLite is copied with the stdlib online backup API (never a raw copy of an open DB/WAL) and
# integrity-checked. Config and trust files are copied as-is, and their inode/mtime is compared
# before and after; any change, missing file, failure or SIGTERM/SIGINT/SIGHUP deletes the partial
# snapshot (it may hold keys), stamps BACKUP_DIR/last-failure and exits nonzero so the operator alert fires.
# A run writes into BACKUP_DIR/.partial-gateway-*, and renames it to gateway-* only after COMPLETE, so
# a SIGKILLed run (no trap) never leaves a gateway-* snapshot; the next run deletes such leftovers
# (older than an hour, so a concurrent run is not disturbed) and the freshness check ignores them. Directories
# are 0700, files 0600; seven complete snapshots are kept. Snapshots hold key material: keep them
# host-local unless an operator-approved encrypted transfer exists. Never upload them to a public
# share or an unencrypted sync. Linked OS ancestors above STATE_DIR (macOS /var -> /private/var) are
# resolved; a linked STATE_DIR or anything under it still refuses.
set -euo pipefail
umask 077

[[ $# -ge 2 && $# -le 3 ]] || { echo "usage: $0 BACKUP_DIR CONFIG_FILE [STATE_DIR]" >&2; exit 2; }
BACKUP_DIR=$1
CONFIG_FILE=$2
STATE_DIR=${3:-/var/lib/openshell}
KEEP=7

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
name="gateway-$(date -u +%Y%m%dT%H%M%SZ)"
snapshot="$BACKUP_DIR/$name"
partial=""
created=false
succeeded=false
# Any exit before last-success is stamped (a failure, or SIGTERM/SIGINT/SIGHUP from a job runner)
# marks this run's snapshot INVALID, deletes it (it may already hold keys) and stamps last-failure,
# so an interrupted run never leaves a usable-looking snapshot or a fresh last-success.
on_exit() {
  $succeeded && return
  if $created && [[ -d "$partial" ]]; then
    : > "$partial/INVALID" 2>/dev/null || true  # if deletion fails, the next run's pruning removes it
    rm -rf -- "$partial"
  fi
  date -u +%Y-%m-%dT%H:%M:%SZ > "$BACKUP_DIR/last-failure" 2>/dev/null || true
}
on_signal() {
  echo "backup_interrupted: partial snapshot deleted" >&2
  exit "$1"
}
trap on_exit EXIT
trap 'on_signal 129' HUP
trap 'on_signal 130' INT
trap 'on_signal 143' TERM
[[ ! -e "$snapshot" ]] || { echo "backup_failed: $snapshot already exists" >&2; exit 1; }
partial=$(mktemp -d "$BACKUP_DIR/.partial-$name.XXXXXX")
created=true

python3 - "$STATE_DIR" "$CONFIG_FILE" "$partial" <<'PY' || { echo "backup_failed: partial snapshot deleted" >&2; exit 1; }
import hashlib, os, shutil, sqlite3, stat, sys
def anchored(path):
    """Resolve OS-level links above the leaf (macOS /var -> /private/var) but keep the leaf itself."""
    path = os.path.abspath(path)
    return os.path.join(os.path.realpath(os.path.dirname(path)), os.path.basename(path))

state, config, out = anchored(sys.argv[1]), anchored(sys.argv[2]), sys.argv[3]
REQUIRED = ["tls/gateway.pem", "tls/gateway-key.pem", "tls/client-ca.pem",
            "tls/jwt/signing.pem", "tls/jwt/public.pem", "tls/jwt/kid"]

def regular(path):
    return os.path.lexists(path) and stat.S_ISREG(os.lstat(path).st_mode)

def linked(path):
    """True when the path or any existing ancestor is a symlink (a linked trust root is not coverage)."""
    path = os.path.abspath(path)
    while True:
        if os.path.islink(path):
            return True
        parent = os.path.dirname(path)
        if parent == path:
            return False
        path = parent

missing = [p for p in ["gateway.db", *REQUIRED] if not regular(os.path.join(state, p)) or linked(os.path.join(state, p))]
if not regular(config) or linked(config):
    missing.append("CONFIG_FILE")
if missing:
    sys.exit("coverage_incomplete: " + ", ".join(missing))

def trust_files():
    files = [config]
    for root, dirs, names in os.walk(state):
        dirs.sort()
        for name in sorted(names):
            path = os.path.join(root, name)
            if name.startswith("gateway.db") or not stat.S_ISREG(os.lstat(path).st_mode):
                continue  # the database goes through the online backup API; links are not followed
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
    dest = os.path.join(out, "config", os.path.basename(path)) if path == config \
        else os.path.join(out, "state", os.path.relpath(path, state))
    os.makedirs(os.path.dirname(dest), mode=0o700, exist_ok=True)
    shutil.copy2(path, dest)
if fingerprint(trust_files()) != before:
    sys.exit("trust_files_changed_during_backup")
for required in REQUIRED:
    if not os.path.isfile(os.path.join(out, "state", required)):
        sys.exit("coverage_incomplete_in_snapshot: " + required)
if not os.path.isfile(os.path.join(out, "config", os.path.basename(config))):
    sys.exit("coverage_incomplete_in_snapshot: CONFIG_FILE")
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

: > "$partial/COMPLETE"
# Atomic publish: only a COMPLETE snapshot ever carries the gateway-* name.
mv -- "$partial" "$snapshot"
# Atomic stamp: an interruption leaves either the old last-success or the new one, never a torn file.
date -u +%Y-%m-%dT%H:%M:%SZ > "$BACKUP_DIR/last-success.tmp"
mv -f -- "$BACKUP_DIR/last-success.tmp" "$BACKUP_DIR/last-success"
succeeded=true
# Keep the newest complete snapshots. INVALID or incomplete ones (older versions, SIGKILL) hold keys and
# are deleted, as are partial dirs a SIGKILLed run left behind.
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '.partial-gateway-*' -mmin +60 -exec rm -rf -- {} + 2>/dev/null || true
kept=0
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name 'gateway-*' | sort -r | while IFS= read -r dir; do
  [[ ! -f "$dir/INVALID" && -f "$dir/COMPLETE" ]] || { rm -rf -- "$dir"; continue; }
  kept=$((kept + 1))
  [[ $kept -le $KEEP ]] || rm -rf -- "$dir"
done
echo "OK: $snapshot"
