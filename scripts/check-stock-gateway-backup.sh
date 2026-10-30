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
#
#   check-stock-gateway-backup.sh --plain-restore-eligibility SNAPSHOT --state-dir STATE_DIR
#
# #503 plain branch (no restore is performed): only a SNAPSHOT_MODE=plain snapshot with no control/ directory, the
# same complete inventory/integrity/config/trust checks, an explicit key_encryption_key_path (an env-only or default
# KEK refuses) and the fixed signer CA, each required in the inventory at exactly its configured path under the
# explicit original STATE_DIR (mapped lexically, never through a link below it), then a fresh authenticated rollback preflight
# (FLEET_ORCHESTRATOR_URL / ORCHESTRATOR_AUTH_TOKEN, token via curl stdin config, 10 s) that must report exactly the
# four counts with subscriptionBindings=0 and unresolvedBindings=0; anything else is REFUSED: subscription_state_unverified.
# Both branches require the matching SNAPSHOT_MODE marker (missing/unknown/other refuses; no downgrade either way) and,
# for a config with the [[openshell.supervisor.middleware]] name = "stock-memory" registration, its tls_ca_cert_path
# under STATE_DIR in the verified inventory (else REFUSED: signer_tls_ca_not_covered); the subscription branch also
# compares the manifest's stateDirectory and signer fields, which are explicit nulls without a registration.
#
#   check-stock-gateway-backup.sh --require-subscription-recovery BACKUP_DIR [MAX_AGE_HOURS]
#
# #503 N2 monitor while subscription custody may exist: the same staleness checks (exit 1), then the newest complete
# snapshot in BACKUP_DIR only is classified, always as a warning with exit 3: plain or unmarked is
# WARN: subscription_recovery_unqualified, control-bearing is WARN: subscription_recovery_unverified. Daily positional
# backups never qualify subscription recovery; without the flag a pass means freshness only, not restore eligibility.
set -euo pipefail

if [[ "${1:-}" == "--restore-eligibility" || "${1:-}" == "--plain-restore-eligibility" ]]; then
  if [[ "$1" == "--restore-eligibility" ]]; then
    [[ $# -eq 8 && "$3" == "--subscription-source" && "$5" == "--managed-binding" && "$7" == "--refresh-observation" ]] \
      || { echo "usage: $0 --restore-eligibility SNAPSHOT --subscription-source FILE --managed-binding FILE --refresh-observation FILE" >&2; exit 2; }
    set -- subscription "$2" "$4" "$6" "$8"
  else
    [[ $# -eq 4 && "$3" == "--state-dir" ]] || { echo "usage: $0 --plain-restore-eligibility SNAPSHOT --state-dir STATE_DIR" >&2; exit 2; }
    set -- plain "$2" "$4"
  fi
  MODE=$1
  python3 - "$@" <<'PY' || exit $?
import datetime, hashlib, json, os, re, sqlite3, stat, sys
mode, snapshot = sys.argv[1:3]

def refuse(reason):
    sys.exit(f"REFUSED: {reason}")

def anchored(path):
    """Resolve OS-level links above the leaf (macOS /var -> /private/var) but keep the leaf itself, as the backup does."""
    path = os.path.abspath(path)
    return os.path.join(os.path.realpath(os.path.dirname(path)), os.path.basename(path))

def regular(path):
    return os.path.lexists(path) and stat.S_ISREG(os.lstat(path).st_mode)

def strict(raw):
    def pairs(items):
        keys = [k for k, _ in items]
        if len(keys) != len(set(keys)):
            raise ValueError("duplicate key")
        return dict(items)
    return json.loads(raw, object_pairs_hook=pairs)

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
            # #503 P1: an unsupported header (quoted, escaped or spaced keys), a non-bare key or a continuation line is
            # never skipped: tomllib may read it as relevant data, so the fallback fails closed instead.
            if line and (line.startswith("[") or "=" not in line or not re.fullmatch(r"[A-Za-z0-9_.-]+", key)):
                return unknown
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

def state_relative(path, state_dir):
    """#503 CA1: a configured file's location under STATE_DIR, mapped lexically. Only OS links ABOVE STATE_DIR are
    resolved (macOS /var -> /private/var), never a component below it, so a linked alias can never stand in for the
    configured path. None when the path is not canonical-absolute or not under STATE_DIR."""
    if not isinstance(path, str) or not os.path.isabs(path) or os.path.normpath(path) != path:
        return None
    root, parts = anchored(state_dir), path.strip("/").split("/")
    for depth in range(1, len(parts)):
        if anchored("/" + "/".join(parts[:depth])) == root:
            return "/".join(parts[depth:])
    return None

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
# #503 F1: the recorded mode marker decides the branch; it is never inferred, and there is no downgrade either way.
marker = open(os.path.join(snapshot, "SNAPSHOT_MODE"), "rb").read() if "SNAPSHOT_MODE" in listed else None
if marker not in (b"plain\n", b"subscription\n"):
    refuse("snapshot mode marker missing or unknown (take a fresh qualified checkpoint)")
if marker != (mode + "\n").encode():
    refuse(f"snapshot mode marker is {marker.decode().strip()}, not {mode}: no mode downgrade or cross-branch restore")
if mode == "plain" and (os.path.lexists(os.path.join(snapshot, "control")) or any(n.startswith("control/") for n in listed)):
    refuse("plain eligibility refuses a control-bearing snapshot")
REQUIRED = ["gateway.db", "state/tls/gateway.pem", "state/tls/gateway-key.pem", "state/tls/ca.pem", "state/tls/client-ca.pem",
            "state/tls/jwt/signing.pem", "state/tls/jwt/public.pem", "state/tls/jwt/kid"]
CONTROL = ["control/subscription.json", "control/applied-runtime-binding.json", "control/refresh-observation.json", "control/MANIFEST.json"]
if any(name not in listed for name in REQUIRED + (CONTROL if mode == "subscription" else [])) \
        or len([n for n in listed if n.startswith("config/")]) != 1:
    refuse("snapshot lacks required gateway DB/config/trust or control coverage")
try:
    database = sqlite3.connect(f"file:{os.path.join(snapshot, 'gateway.db')}?mode=ro", uri=True)
    healthy = database.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    database.close()
except sqlite3.Error:
    healthy = False
if not healthy:
    refuse("snapshot gateway DB integrity check failed")
config_name = next(n for n in listed if n.startswith("config/"))
kek_mode, kek_path, signer_mode, signer_config = gateway_config(os.path.join(snapshot, config_name))

def covered(path, state_dir):
    """The inventory entry of exactly the configured path under STATE_DIR (mapped lexically, never through a link below
    it), or None when noncanonical, outside or unlisted."""
    rel = state_relative(path, state_dir)
    entry = None if rel is None else "state/" + rel
    return entry if entry in listed else None

def signer_entry(state_dir):
    """#503 H1: None without the fixed registration; otherwise its CA's verified inventory entry, else refused."""
    if signer_mode == "none":
        return None
    if signer_mode != "registered" or not signer_config or covered(signer_config, state_dir) is None:
        refuse("signer_tls_ca_not_covered")
    return covered(signer_config, state_dir)

if mode == "plain":
    # #503: explicit operator-selected original STATE_DIR; CA/KEK mapping derived from the snapshot config against it.
    state_dir = sys.argv[3]
    if not os.path.isabs(state_dir):
        refuse("STATE_DIR must be the absolute original gateway state directory")
    state_dir = anchored(state_dir)
    # #503 B1: an env-only or default KEK leaves no key in the snapshot, so it is never restore-eligible here.
    if kek_mode != "path" or covered(kek_path, state_dir) is None:
        refuse("configured KEK is not covered by the snapshot inventory (plain restore needs an explicit key_encryption_key_path)")
    signer_entry(state_dir)
    sys.exit(0)  # the live no-custody proof follows in the caller

source_path, binding_path, refresh_path = sys.argv[3:6]
try:
    manifest = strict(open(os.path.join(snapshot, "control", "MANIFEST.json"), "rb").read().decode("utf-8"))
    recorded = strict(open(os.path.join(snapshot, "control", "applied-runtime-binding.json"), "rb").read().decode("utf-8"))
except (ValueError, UnicodeDecodeError):
    refuse("snapshot control records unreadable")
if manifest.get("bindingSha256") != listed["control/applied-runtime-binding.json"] \
        or manifest.get("sourceSha256") != listed["control/subscription.json"]:
    refuse("snapshot manifest does not match its control records")
# Review B1: the KEK the snapshot's own gateway config names must be in the verified inventory.
kek_entry = manifest.get("keyEncryptionKey")
if kek_mode != "path" or kek_path != manifest.get("keyEncryptionKeyConfigPath") or not isinstance(kek_entry, str) \
        or not kek_entry.startswith("state/") or kek_entry not in listed:
    refuse("configured KEK is not covered by the snapshot inventory")
# #503: signer metadata against the parsed snapshot config and recorded STATE_DIR; explicit nulls only without a registration.
state_dir = manifest.get("stateDirectory")
signer_keys = ("signerRegistration", "signerCaConfigPath", "signerCaEntry")
if not isinstance(state_dir, str) or not os.path.isabs(state_dir) or any(key not in manifest for key in signer_keys):
    refuse("snapshot manifest lacks the recorded STATE_DIR or signer metadata")
entry = signer_entry(state_dir)
expected = (None, None, None) if entry is None else ("stock-memory", signer_config, entry)
if tuple(manifest[key] for key in signer_keys) != expected:
    refuse("signer metadata does not match the snapshot config")

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
  [[ "$MODE" == "plain" ]] || exit 0
  # #503 F1: a fresh authenticated no-custody proof at invocation, never a cached count or file. The token goes
  # through curl's stdin config (-K -), never argv or logs.
  unverified() { echo "REFUSED: subscription_state_unverified ($1)" >&2; exit 1; }
  token=${ORCHESTRATOR_AUTH_TOKEN:-}
  [[ -n "$token" ]] || unverified "no operator token configured"
  url=${FLEET_ORCHESTRATOR_URL:-http://127.0.0.1:3600}
  esc=${token//\\/\\\\}; esc=${esc//\"/\\\"}
  body=$(printf 'header = "Authorization: Bearer %s"\n' "$esc" \
    | curl -fsS --max-time 10 -K - "$url/api/managed-runtime/rollback-preflight") || unverified "rollback preflight unavailable"
  python3 - "$body" <<'PY' || unverified "subscription custody or unresolved bindings present, or the answer is invalid"
import json, sys
try:
    data = json.loads(sys.argv[1])
except ValueError:
    sys.exit(1)
keys = {"desiredOpenShell", "appliedOpenShell", "unresolvedBindings", "subscriptionBindings"}
if not isinstance(data, dict) or set(data) != keys or any(type(data[k]) is not int or data[k] < 0 for k in keys):
    sys.exit(1)
if data["subscriptionBindings"] or data["unresolvedBindings"]:
    print("managed counts: " + ", ".join(f"{k}={data[k]}" for k in sorted(keys)), file=sys.stderr)
    sys.exit(1)
PY
  echo "ELIGIBLE: plain snapshot with live proof of no subscription custody; restore only gateway DB/KEK/config/trust while stopped"
  exit 0
fi

REQUIRE_RECOVERY=0
if [[ "${1:-}" == "--require-subscription-recovery" ]]; then REQUIRE_RECOVERY=1; shift; fi
[[ $# -ge 1 && $# -le 2 ]] || { echo "usage: $0 [--require-subscription-recovery] BACKUP_DIR [MAX_AGE_HOURS]" >&2; exit 2; }
BACKUP_DIR=$1
MAX_AGE_HOURS=${2:-26}
[[ "$MAX_AGE_HOURS" =~ ^[1-9][0-9]*$ ]] || { echo "usage: MAX_AGE_HOURS must be a positive integer" >&2; exit 2; }

python3 - "$BACKUP_DIR" "$MAX_AGE_HOURS" "$REQUIRE_RECOVERY" <<'PY'
import datetime, os, sys
backup_dir, max_hours, require_recovery = sys.argv[1], int(sys.argv[2]), sys.argv[3] == "1"

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
snapshots = sorted(name for name in os.listdir(backup_dir) if complete(name))
if not snapshots:
    sys.exit(f"STALE: no complete gateway snapshot in {backup_dir}")
if not require_recovery:
    print(f"OK: last gateway backup {int(age.total_seconds() // 60)}m ago (freshness only; restore eligibility not assessed)")
    sys.exit(0)
# #503 N2: classify only the newest complete snapshot in this directory; never discover controls elsewhere. Every
# outcome is a warning (exit 3): only a separately authorized eligibility run can qualify subscription recovery.
marker = os.path.join(backup_dir, snapshots[-1], "SNAPSHOT_MODE")
mode = open(marker, "rb").read() if os.path.isfile(marker) and not os.path.islink(marker) else None
if mode == b"subscription\n":
    print(f"WARN: subscription_recovery_unverified: newest snapshot {snapshots[-1]} carries control records; current restore eligibility is not assessed")
else:
    print(f"WARN: subscription_recovery_unqualified: newest snapshot {snapshots[-1]} is plain or unmarked, not a subscription-recovery checkpoint")
sys.exit(3)
PY
