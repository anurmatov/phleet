#!/usr/bin/env bash
# Daily protected backup of the stock OpenShell gateway state (#492 §16). Run from the operator's
# daily host backup job and before any gateway configuration change or upgrade.
#
#   backup-stock-gateway.sh BACKUP_DIR CONFIG_FILE [STATE_DIR]   # STATE_DIR defaults to /var/lib/openshell
#   backup-stock-gateway.sh BACKUP_DIR CONFIG_FILE [STATE_DIR] \
#     --subscription-source FILE --managed-binding FILE --refresh-observation FILE   # #502 subscription
#
# #502: a managed subscription fixture additionally supplies, all three or none, the outside
# subscription.json, the operator-exported byte copy of the owned agent's applied-runtime-binding JSON
# (the CAS value) and the independently retained refresh observation. They are copied as EVIDENCE ONLY
# to control/ with control/MANIFEST.json (capture time, identities, source/binding digests, native
# epochs, refresh times); restore never imports them. A missing, foreign-owned, group/other-accessible,
# symlinked, inconsistent or changed control input fails without COMPLETE. No live DB query or
# credential read is added; API/manual positional invocation is unchanged. A key-encryption key configured
# as [openshell.gateway.credential_storage] key_encryption_key_path must exist under STATE_DIR for every
# backup; subscription backups require that explicit path (an env-only or default KEK refuses).
# #503: a config with the fixed [[openshell.supervisor.middleware]] name = "stock-memory" signer registration needs its
# explicit absolute tls_ca_cert_path as a regular, non-empty, non-linked file under STATE_DIR in every backup, else
# "coverage_incomplete: signer_tls_ca"; malformed, duplicate or ambiguous registration data fails the same way rather
# than reading as absent (both the tomllib and Python < 3.11 parsers). Every snapshot records SNAPSHOT_MODE (plain for
# a positional run, subscription with the three control inputs) inside SHA256SUMS; subscription manifests also record
# stateDirectory and signerRegistration/signerCaConfigPath/signerCaEntry (explicit nulls without a registration).
# Pruning keeps seven complete snapshots inside the invoked BACKUP_DIR only: maintenance S0/S1 checkpoints go to their
# own once-used directories outside the daily tree (docs/openshell-stock.md), never into it.
#
# Coverage is complete or the run fails: gateway.db, the gateway TOML and every trust file it uses
# (TLS server cert/key, the gateway CA guest_tls_ca names, client CA, JWT signing/public key and kid
# under STATE_DIR/tls) must exist.
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

usage() { echo "usage: $0 BACKUP_DIR CONFIG_FILE [STATE_DIR] [--subscription-source FILE --managed-binding FILE --refresh-observation FILE]" >&2; exit 2; }
positional=()
SUB_SOURCE="" SUB_BINDING="" SUB_REFRESH=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --subscription-source) [[ $# -ge 2 ]] || usage; SUB_SOURCE=$2; shift 2 ;;
    --managed-binding) [[ $# -ge 2 ]] || usage; SUB_BINDING=$2; shift 2 ;;
    --refresh-observation) [[ $# -ge 2 ]] || usage; SUB_REFRESH=$2; shift 2 ;;
    --*) usage ;;
    *) positional+=("$1"); shift ;;
  esac
done
[[ ${#positional[@]} -ge 2 && ${#positional[@]} -le 3 ]] || usage
if [[ -n "$SUB_SOURCE$SUB_BINDING$SUB_REFRESH" && ( -z "$SUB_SOURCE" || -z "$SUB_BINDING" || -z "$SUB_REFRESH" ) ]]; then
  echo "usage: --subscription-source, --managed-binding and --refresh-observation go together" >&2; exit 2
fi
BACKUP_DIR=${positional[0]}
CONFIG_FILE=${positional[1]}
STATE_DIR=${positional[2]:-/var/lib/openshell}
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

python3 - "$STATE_DIR" "$CONFIG_FILE" "$partial" "$SUB_SOURCE" "$SUB_BINDING" "$SUB_REFRESH" <<'PY' || { echo "backup_failed: partial snapshot deleted" >&2; exit 1; }
import datetime, hashlib, json, os, re, shutil, sqlite3, stat, sys
def anchored(path):
    """Resolve OS-level links above the leaf (macOS /var -> /private/var) but keep the leaf itself."""
    path = os.path.abspath(path)
    return os.path.join(os.path.realpath(os.path.dirname(path)), os.path.basename(path))

state, config, out = anchored(sys.argv[1]), anchored(sys.argv[2]), sys.argv[3]
control = [anchored(p) for p in sys.argv[4:7]] if sys.argv[4] else []
REQUIRED = ["tls/gateway.pem", "tls/gateway-key.pem", "tls/ca.pem", "tls/client-ca.pem",
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

def gateway_config(config_path):
    """The gateway's [openshell.gateway.credential_storage] KEK, ("path", abs) | ("env"|"none"|"unknown", None), and its
    fixed [[openshell.supervisor.middleware]] name = "stock-memory" signer registration, ("registered", abs CA path or
    None) | ("none", None) | ("unknown", None). Malformed, duplicate or ambiguous relevant data is "unknown", never absence."""
    text = open(config_path, encoding="utf-8").read()
    unknown = ("unknown", None, "unknown", None)
    invalid = object()
    try:
        import tomllib
        try:
            data = tomllib.loads(text)
        except tomllib.TOMLDecodeError:
            return unknown
        def table(*keys):
            node = data
            for key in keys:
                if not isinstance(node, dict):
                    return invalid
                node = node.get(key)
                if node is None:
                    return None
            return node
        storage = table("openshell", "gateway", "credential_storage")
        middleware = table("openshell", "supervisor", "middleware")
        if storage is invalid or middleware is invalid:
            return unknown
    except ModuleNotFoundError:  # Python < 3.11: plain tables and [[arrays]] with basic-string/integer/boolean values only
        storage, middleware, current = None, None, None
        for raw in text.splitlines():
            line = raw.split("#", 1)[0].strip()
            array = re.fullmatch(r"\[\[\s*([A-Za-z0-9_.-]+)\s*\]\]", line)
            header = array or re.fullmatch(r"\[\s*([A-Za-z0-9_.-]+)\s*\]", line)
            if header:
                name, current = header.group(1), None
                if name == "openshell.gateway.credential_storage" and not array and storage is None:
                    storage = current = {}
                elif name == "openshell.supervisor.middleware" and array:
                    middleware = [] if middleware is None else middleware
                    current = {}
                    middleware.append(current)
                elif name.startswith(("openshell.gateway.credential_storage", "openshell.supervisor")):
                    return unknown  # a duplicate, mis-kinded or nested relevant table
                continue
            key = line.split("=", 1)[0].strip()
            if "credential_storage" in line or "middleware" in key or re.match(r"(openshell\.)?supervisor\b", key):
                return unknown  # dotted keys or inline tables for relevant data are not parsed here
            if current is not None and line:
                pair = re.fullmatch(r'([A-Za-z0-9_]+)\s*=\s*("[^"\\]*"|-?[0-9]+|true|false)', line)
                if not pair or pair.group(1) in current:
                    return unknown
                value = pair.group(2)
                current[pair.group(1)] = value[1:-1] if value.startswith('"') else value == "true" if value in ("true", "false") else int(value)
    if storage is None:
        kek = ("none", None)
    elif not isinstance(storage, dict):
        kek = ("unknown", None)
    elif "key_encryption_key_env" in storage:
        kek = ("env", None)
    else:
        path = storage.get("key_encryption_key_path")
        kek = ("path", path) if isinstance(path, str) and os.path.isabs(path) else ("unknown", None)
    if middleware is None:
        signer = ("none", None)
    elif not isinstance(middleware, list) or not all(isinstance(m, dict) and isinstance(m.get("name"), str) for m in middleware):
        signer = ("unknown", None)
    else:
        named = [m for m in middleware if m["name"] == "stock-memory"]
        ca = named[0].get("tls_ca_cert_path") if len(named) == 1 else None
        signer = ("none", None) if not named else ("unknown", None) if len(named) > 1 \
            else ("registered", ca if isinstance(ca, str) and os.path.isabs(ca) else None)
    return (*kek, *signer)

# #502 review B1: a configured key-encryption key is part of coverage (the DB's provider credentials are
# unrecoverable without it). It must be a regular, non-linked file under STATE_DIR; subscription snapshots
# additionally need it configured explicitly by path, since an env or default KEK cannot be covered here.
config_ok = regular(config) and not linked(config)
kek_mode, kek_path, signer_mode, signer_config = gateway_config(config) if config_ok else ("unknown", None, "unknown", None)
kek_rel, kek_config = None, kek_path
if kek_mode == "path":
    kek_path = anchored(kek_path)
    kek_rel = os.path.relpath(kek_path, state)
# #503 H1: the fixed stock-memory signer registration needs its explicit CA as a regular, non-empty, non-linked file
# under STATE_DIR in every backup (positional or subscription); malformed or ambiguous registration data is never absence.
signer_rel = None
if signer_mode == "registered" and signer_config:
    signer_path = anchored(signer_config)
    signer_rel = os.path.relpath(signer_path, state)
missing = [p for p in ["gateway.db", *REQUIRED] if not regular(os.path.join(state, p)) or linked(os.path.join(state, p))]
if kek_mode == "path" and (kek_rel.startswith("..") or not regular(kek_path) or linked(kek_path)):
    missing.append("key_encryption_key_path")
if sys.argv[4] and kek_mode != "path":
    missing.append("key_encryption_key_path (subscription snapshots need an explicit KEK path under STATE_DIR)")
if config_ok and (signer_mode == "unknown" or signer_mode == "registered" and (signer_rel is None or signer_rel.startswith("..")
        or not regular(signer_path) or linked(signer_path) or os.lstat(signer_path).st_size == 0)):
    missing.append("signer_tls_ca")
if not regular(config) or linked(config):
    missing.append("CONFIG_FILE")
if missing:
    sys.exit("coverage_incomplete: " + ", ".join(missing))

# #502 control inputs: evidence-only copies, never restored or imported.
CONTROL_NAMES = ["subscription.json", "applied-runtime-binding.json", "refresh-observation.json"]
def strict(raw):
    def pairs(items):
        keys = [k for k, _ in items]
        if len(keys) != len(set(keys)):
            raise ValueError("duplicate key")
        return dict(items)
    return json.loads(raw, object_pairs_hook=pairs)
def utc(value, nullable=False):
    if value is None and nullable:
        return None
    if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z", value):
        raise ValueError("timestamp")
    return value
def control_manifest(paths):
    for path in paths:
        st = os.lstat(path) if os.path.lexists(path) else None
        if st is None or not stat.S_ISREG(st.st_mode) or linked(path):
            sys.exit("control_input_invalid: missing or not a regular non-linked file")
        if st.st_uid != os.geteuid() or st.st_mode & 0o077:
            sys.exit("control_input_invalid: foreign owner or group/other access")
    raw = [open(p, "rb").read() for p in paths]
    try:
        source, binding, refresh = (strict(r.decode("utf-8")) for r in raw)
        if set(refresh) != {"providerId", "ownerGeneration", "statusObservedAtUtc", "providerLastRefreshAtUtc", "nextRefreshAtUtc"}:
            raise ValueError("refresh fields")
        utc(refresh["statusObservedAtUtc"]); utc(refresh["nextRefreshAtUtc"]); utc(refresh["providerLastRefreshAtUtc"], nullable=True)
        if source.get("version") != 1 or source.get("purpose") != "compatibility_only":
            raise ValueError("source")
        identity = (source["providerId"], source["ownerGeneration"])
        if (refresh["providerId"], refresh["ownerGeneration"]) != identity:
            raise ValueError("refresh observation names another provider/owner generation")
        tuples = [b for b in (binding.get("applied"), binding.get("candidate"), *(binding.get("superseded") or [])) if b]
        if not any((t.get("subscription") or {}).get("providerId") == source["providerId"]
                   and (t.get("subscription") or {}).get("ownerGeneration") == source["ownerGeneration"]
                   and t.get("sandboxWorkspace") == source["sandboxWorkspace"] for t in tuples):
            raise ValueError("binding row does not hold the source identity")
    except (ValueError, KeyError, TypeError, UnicodeDecodeError, AttributeError) as error:
        sys.exit(f"control_input_invalid: {error}")
    epoch = lambda b: (binding.get(b) or {}).get("epoch")
    return raw, {
        # Sub-second capture time, so a refresh later in the same second still supersedes it.
        "snapshotCapturedAtUtc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
        "gatewayGeneration": source["gatewayGeneration"], "providerId": source["providerId"],
        "providerWorkspace": source["providerWorkspace"], "sandboxWorkspace": source["sandboxWorkspace"],
        "ownerGeneration": source["ownerGeneration"], "sourceSha256": hashlib.sha256(raw[0]).hexdigest(),
        "bindingSha256": hashlib.sha256(raw[1]).hexdigest(), "bindingState": binding.get("state"),
        "retiredSubscriptions": len(binding.get("retiredSubscriptions") or []),
        "nativeAppliedEpoch": epoch("applied"), "nativeCandidateEpoch": epoch("candidate"),
        "statusObservedAtUtc": refresh["statusObservedAtUtc"], "providerLastRefreshAtUtc": refresh["providerLastRefreshAtUtc"],
        "nextRefreshAtUtc": refresh["nextRefreshAtUtc"],
        "keyEncryptionKey": "state/" + kek_rel, "keyEncryptionKeyConfigPath": kek_config,
        # #503: the recorded STATE_DIR and the parsed registration; all three signer fields are null without one.
        "stateDirectory": state, "signerRegistration": "stock-memory" if signer_rel else None,
        "signerCaConfigPath": signer_config if signer_rel else None, "signerCaEntry": "state/" + signer_rel if signer_rel else None,
    }
control_before = {p: (os.lstat(p).st_ino, os.lstat(p).st_mtime_ns, os.lstat(p).st_size) for p in control}
control_raw, manifest = control_manifest(control) if control else (None, None)

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
if control:
    if {p: (os.lstat(p).st_ino, os.lstat(p).st_mtime_ns, os.lstat(p).st_size) for p in control} != control_before \
            or [open(p, "rb").read() for p in control] != control_raw:
        sys.exit("control_inputs_changed_during_backup")
    os.makedirs(os.path.join(out, "control"), mode=0o700)
    for name, data in zip(CONTROL_NAMES, control_raw):
        with open(os.path.join(out, "control", name), "wb") as handle:
            handle.write(data)
    with open(os.path.join(out, "control", "MANIFEST.json"), "w") as handle:
        json.dump(manifest, handle, indent=1, sort_keys=True)
for required in REQUIRED + ([kek_rel] if kek_rel else []):
    if not os.path.isfile(os.path.join(out, "state", required)):
        sys.exit("coverage_incomplete_in_snapshot: " + required)
if signer_rel and not os.path.isfile(os.path.join(out, "state", signer_rel)):
    sys.exit("coverage_incomplete: signer_tls_ca")
# #503 F1: the snapshot mode is recorded, never inferred: complete control inputs make it subscription.
with open(os.path.join(out, "SNAPSHOT_MODE"), "w") as handle:
    handle.write("subscription\n" if control else "plain\n")
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
