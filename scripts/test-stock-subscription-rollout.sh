#!/usr/bin/env bash
# #502 focused, inert rollout/snapshot regressions: no live runtime, gateway, credential or network use.
# Runs on GNU and BSD userlands (bash, python3, mktemp; no sed -i, stat -c or sha256sum).
#
#   bash scripts/test-stock-subscription-rollout.sh
#
# Covers: the subscription-capable guard against old-capability and incapable targets (before any checkout
# switch), the exact previous guard (06c30ae2) refusing the four-key preflight while its managed-label
# shortcut skips the query (unsupported, not a safety control), subscription snapshot control records and
# manifest, stale/incomplete/mismatched/changed snapshot refusal, and a synthetic stopped-copy success.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
GUARD="$ROOT/scripts/check-stock-runtime-rollout.sh"
BACKUP="$ROOT/scripts/backup-stock-gateway.sh"
CHECK="$ROOT/scripts/check-stock-gateway-backup.sh"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/stock-subscription-rollout.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
pass=0
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { pass=$((pass + 1)); echo "ok - $*"; }
expect() { # expect <0|1> <description> <command...>
  local want=$1 what=$2 rc=0; shift 2
  "$@" >"$WORK/out" 2>&1 || rc=$?
  if [[ "$want" == 0 && $rc -ne 0 ]] || [[ "$want" != 0 && $rc -eq 0 ]]; then cat "$WORK/out" >&2; fail "$what (rc=$rc)"; fi
  ok "$what"
}

# ── guard: a stub curl answers the preflight; no network ──
mkdir -p "$WORK/bin"
cat > "$WORK/bin/curl" <<'STUB'
#!/usr/bin/env bash
cat >/dev/null
echo "curl" >> "$STUB_CALLS"
printf '%s' "$STUB_BODY"
STUB
chmod 755 "$WORK/bin/curl"
export PATH="$WORK/bin:$PATH" STUB_CALLS="$WORK/calls" ORCHESTRATOR_AUTH_TOKEN=synthetic-operator-token
four() { printf '{"desiredOpenShell":%s,"appliedOpenShell":%s,"unresolvedBindings":%s,"subscriptionBindings":%s}' "$1" "$2" "$3" "$4"; }
printf 'FROM x\nLABEL org.phleet.managed-stock-runtime=1\nLABEL org.phleet.managed-stock-subscription=1\n' > "$WORK/Dockerfile.new"
printf 'FROM x\nLABEL org.phleet.managed-stock-runtime=1\n' > "$WORK/Dockerfile.managed"
printf 'FROM x\n' > "$WORK/Dockerfile.none"

rm -f "$STUB_CALLS"
expect 0 "subscription-capable target passes without a preflight query" bash "$GUARD" --dockerfile "$WORK/Dockerfile.new"
[[ ! -e "$STUB_CALLS" ]] || fail "capable target queried the preflight"
expect 0 "this repository's orchestrator declares both capabilities" bash "$GUARD" --dockerfile "$ROOT/src/Fleet.Orchestrator/Dockerfile"
STUB_BODY=$(four 3 3 0 1) expect 1 "old-capability target refuses while subscriptionBindings>0 (no old-label shortcut)" bash "$GUARD" --dockerfile "$WORK/Dockerfile.managed"
STUB_BODY=$(four 3 3 0 0) expect 0 "old-capability target passes once subscription bindings are drained" bash "$GUARD" --dockerfile "$WORK/Dockerfile.managed"
STUB_BODY=$(four 0 1 0 0) expect 1 "incapable target still needs every count zero" bash "$GUARD" --dockerfile "$WORK/Dockerfile.none"
STUB_BODY=$(four 0 0 0 0) expect 0 "incapable target passes with all four counts zero" bash "$GUARD" --dockerfile "$WORK/Dockerfile.none"
STUB_BODY='{"desiredOpenShell":0,"appliedOpenShell":0,"unresolvedBindings":0}' expect 1 "three-key answer is refused (strict key set)" bash "$GUARD" --dockerfile "$WORK/Dockerfile.managed"
STUB_BODY='{"desiredOpenShell":0,"appliedOpenShell":0,"unresolvedBindings":0,"subscriptionBindings":-1}' expect 1 "negative count refused" bash "$GUARD" --dockerfile "$WORK/Dockerfile.none"

# The exact previous guard (06c30ae2), embedded verbatim so a shallow checkout can run it.
cat > "$WORK/old-guard.sh" <<'OLDGUARD'
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
# The header goes through curl's stdin config (-K -), never argv, so the token is not visible in ps.
esc=${token//\\/\\\\}; esc=${esc//\"/\\\"}
body=$(printf 'header = "Authorization: Bearer %s"\n' "$esc" \
  | curl -fsS --max-time 10 -K - "$url/api/managed-runtime/rollback-preflight") \
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
OLDGUARD
python3 - "$WORK/old-guard.sh" <<'PY' || fail "embedded previous guard is not the exact 06c30ae2 bytes"
import hashlib, sys
assert hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest() == "bb8dcac8415164fa2e51a8d612ddcf1ea8b9565d55a7052b432230ec528fd44b"
PY
STUB_BODY=$(four 0 0 0 0) expect 1 "previous guard refuses the new four-key preflight answer" bash "$WORK/old-guard.sh" --dockerfile "$WORK/Dockerfile.none"
rm -f "$STUB_CALLS"
STUB_BODY=$(four 3 3 0 1) expect 0 "previous guard's managed-label shortcut skips the query (unsupported while subscriptionBindings>0)" bash "$WORK/old-guard.sh" --dockerfile "$WORK/Dockerfile.managed"
[[ ! -e "$STUB_CALLS" ]] || fail "previous guard unexpectedly queried"

# ── snapshot: synthetic stopped gateway state plus subscription control records ──
umask 077
state="$WORK/state"; mkdir -p "$state/tls/jwt" "$WORK/control"
for f in gateway.pem gateway-key.pem ca.pem client-ca.pem jwt/signing.pem jwt/public.pem jwt/kid; do printf 'synthetic %s\n' "$f" > "$state/tls/$f"; done
python3 -c 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute("create table t(x)"); c.execute("insert into t values (1)"); c.commit()' "$state/gateway.db"
printf '[openshell]\nversion = 2\n' > "$WORK/gateway.toml"
# #502 review B1: subscription snapshots need the explicitly configured KEK under STATE_DIR.
mkdir -p "$state/credentials"; printf 'synthetic-kek-32-bytes-not-real!' > "$state/credentials/key-encryption-key.bin"
printf '[openshell]\nversion = 2\n\n[openshell.gateway.credential_storage]\nkey_encryption_key_path = "%s"\n' \
  "$state/credentials/key-encryption-key.bin" > "$WORK/gateway-kek.toml"
printf '[openshell]\nversion = 2\n\n[openshell.gateway.credential_storage]\nkey_encryption_key_env = "SYNTHETIC_KEK_ENV"\n' > "$WORK/gateway-env.toml"
printf '[openshell]\nversion = 2\n\n[openshell.gateway.credential_storage]\nkey_encryption_key_path = "%s"\n' \
  "$state/credentials/absent.bin" > "$WORK/gateway-absent.toml"
PROVIDER=3f2b8c1e-5d4a-4b6f-9e7d-1a2b3c4d5e6f OWNER=4f0c6c8e-6f39-4f5b-9d50-6b1b4b7e2a11
cat > "$WORK/control/subscription.json" <<JSON
{"version":1,"purpose":"compatibility_only","agent":"agent1","providerName":"agent1-codex-sub","providerId":"$PROVIDER","providerWorkspace":"ws-a","sandboxWorkspace":"ws-a","accountId":"synthetic-account_01","ownerGeneration":"$OWNER","gatewayGeneration":"gateway-1@start","profileSha256":"$(printf 'c%.0s' $(seq 1 64))","expiresAt":"2099-01-01T00:00:00Z"}
JSON
binding() { # binding <state> <applied-epoch> [owner-generation]
  printf '{"v":1,"state":"%s","applied":{"sandboxName":"fleet-agent1-aaaaaaaa","sandboxId":"11111111-1111-4111-8111-111111111111","epoch":"%s","authMode":"subscription","provider":"agent1-codex-sub","sandboxWorkspace":"ws-a","subscription":{"providerWorkspace":"ws-a","providerId":"%s","accountId":"synthetic-account_01","ownerGeneration":"%s"}},"candidate":null,"superseded":[],"retiredSubscriptions":[]}' "$1" "$2" "$PROVIDER" "${3:-$OWNER}"
}
now_utc() { python3 -c 'import datetime; print(datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"))'; }
# refuse_for <reason substring> <description> <command...>: a refusal for exactly the named reason.
refuse_for() {
  local reason=$1 what=$2 rc=0; shift 2
  "$@" >"$WORK/out" 2>&1 || rc=$?
  if [[ $rc -eq 0 ]] || ! grep -qF "$reason" "$WORK/out"; then cat "$WORK/out" >&2; fail "$what (rc=$rc, wanted: $reason)"; fi
  ok "$what"
}
EPOCH=22222222-2222-4222-8222-222222222222
binding stopped "$EPOCH" > "$WORK/control/binding.json"
refresh() { printf '{"providerId":"%s","ownerGeneration":"%s","statusObservedAtUtc":"2026-10-30T12:00:00Z","providerLastRefreshAtUtc":%s,"nextRefreshAtUtc":"2026-10-30T12:35:00Z"}' "$PROVIDER" "$OWNER" "$1"; }
refresh '"2000-01-01T00:00:00Z"' > "$WORK/control/refresh.json"
chmod 600 "$WORK/control/"*
CTL=(--subscription-source "$WORK/control/subscription.json" --managed-binding "$WORK/control/binding.json" --refresh-observation "$WORK/control/refresh.json")

expect 0 "positional API/manual backup is unchanged" bash "$BACKUP" "$WORK/plain" "$WORK/gateway.toml" "$state"
plain=$(ls -d "$WORK/plain"/gateway-* | head -n 1)
[[ ! -e "$plain/control" ]] || fail "plain backup grew control records"
expect 1 "subscription backup without an explicit KEK path fails" bash "$BACKUP" "$WORK/nokek" "$WORK/gateway.toml" "$state" "${CTL[@]}"
expect 1 "subscription backup with an env-only KEK fails" bash "$BACKUP" "$WORK/envkek" "$WORK/gateway-env.toml" "$state" "${CTL[@]}"
expect 1 "a configured but missing KEK fails coverage even for a positional backup" bash "$BACKUP" "$WORK/absentkek" "$WORK/gateway-absent.toml" "$state"
expect 0 "subscription backup completes with control records" bash "$BACKUP" "$WORK/sub" "$WORK/gateway-kek.toml" "$state" "${CTL[@]}"
snap=$(ls -d "$WORK/sub"/gateway-* | head -n 1)
for f in COMPLETE control/MANIFEST.json control/subscription.json control/applied-runtime-binding.json control/refresh-observation.json; do
  [[ -f "$snap/$f" ]] || fail "snapshot lacks $f"
done
python3 - "$snap/control/MANIFEST.json" "$PROVIDER" "$EPOCH" <<'PY' || fail "manifest fields"
import json, sys
m = json.load(open(sys.argv[1]))
assert m["providerId"] == sys.argv[2] and m["nativeAppliedEpoch"] == sys.argv[3] and m["providerLastRefreshAtUtc"] == "2000-01-01T00:00:00Z"
assert m["providerWorkspace"] == m["sandboxWorkspace"] == "ws-a" and len(m["sourceSha256"]) == 64 and len(m["bindingSha256"]) == 64
assert m["keyEncryptionKey"] == "state/credentials/key-encryption-key.bin"
assert all("token" not in k.lower() for k in m)
PY
ok "manifest records identities, digests, epochs and refresh times (no credential values)"
expect 0 "freshness check accepts the complete subscription snapshot" bash "$CHECK" "$WORK/sub"
# Current inputs for eligibility: the refresh observation is taken AFTER the snapshot capture.
observe() { # observe <last-refresh-json> [observed-at] > file
  printf '{"providerId":"%s","ownerGeneration":"%s","statusObservedAtUtc":"%s","providerLastRefreshAtUtc":%s,"nextRefreshAtUtc":"2099-01-01T00:00:00Z"}' \
    "$PROVIDER" "$OWNER" "${2:-$(now_utc)}" "$1"
}
# eligible <refresh-file> [binding-file] [source-file]; eligible_at <snapshot> <refresh-file> (bash 3.2-safe, no mapfile).
eligible() {
  bash "$CHECK" --restore-eligibility "$snap" --subscription-source "${3:-$WORK/control/subscription.json}" \
    --managed-binding "${2:-$WORK/control/binding.json}" --refresh-observation "$1"
}
eligible_at() {
  bash "$CHECK" --restore-eligibility "$1" --subscription-source "$WORK/control/subscription.json" \
    --managed-binding "$WORK/control/binding.json" --refresh-observation "$2"
}
observe '"2000-01-01T00:00:00Z"' > "$WORK/control/now.json"
expect 0 "synthetic stopped copy is eligible against unchanged current inputs" eligible "$WORK/control/now.json"

observe '"2099-01-01T00:00:00Z"' > "$WORK/control/later.json"
refuse_for "superseded" "snapshot captured before the latest provider refresh is refused" eligible "$WORK/control/later.json"
captured=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["snapshotCapturedAtUtc"])' "$snap/control/MANIFEST.json")
later_in_second=$(python3 - "$captured" <<'PY'
import datetime, sys
base, _, fraction = sys.argv[1][:-1].partition(".")
moment = datetime.datetime.strptime(base, "%Y-%m-%dT%H:%M:%S") + datetime.timedelta(microseconds=int((fraction + "000000")[:6]))
print((moment + datetime.timedelta(microseconds=500)).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z")  # 7 fractional digits
PY
)
observe "\"$later_in_second\"" > "$WORK/control/later-fraction.json"
refuse_for "superseded" "a refresh 500 us after capture (sub-second, 7 digits) is refused" eligible "$WORK/control/later-fraction.json"
# Review M1/F1 exact case: a seconds-only capture and a refresh later in that same second.
cp -R "$snap" "$WORK/seconds-only"
python3 - "$WORK/seconds-only" <<'PY'
import hashlib, json, os, sys
root = sys.argv[1]
path = os.path.join(root, "control", "MANIFEST.json")
manifest = json.load(open(path))
manifest["snapshotCapturedAtUtc"] = "2026-10-30T07:14:30Z"
open(path, "w").write(json.dumps(manifest))
lines = [l for l in open(os.path.join(root, "SHA256SUMS")).read().splitlines() if not l.endswith("  control/MANIFEST.json")]
lines.append(hashlib.sha256(open(path, "rb").read()).hexdigest() + "  control/MANIFEST.json")
open(os.path.join(root, "SHA256SUMS"), "w").write("\n".join(lines) + "\n")
PY
observe '"2026-10-30T07:14:30.500Z"' > "$WORK/control/same-second.json"
refuse_for "superseded" "seconds-only capture with a refresh at .500 in the same second is refused" eligible_at "$WORK/seconds-only" "$WORK/control/same-second.json"
observe '""' > "$WORK/control/empty-last.json"
refuse_for "current last refresh is missing or not a UTC timestamp" "an empty last-refresh value is refused" eligible "$WORK/control/empty-last.json"
printf '{"providerId":"%s","ownerGeneration":"%s","providerLastRefreshAtUtc":null,"nextRefreshAtUtc":"2099-01-01T00:00:00Z"}' "$PROVIDER" "$OWNER" > "$WORK/control/no-observed.json"
refuse_for "freshness unknown" "a missing observation time is refused" eligible "$WORK/control/no-observed.json"
observe '"2000-01-01T00:00:00Z"' "2000-01-02T00:00:00Z" > "$WORK/control/old-observation.json"
refuse_for "predates the snapshot" "an observation taken before the capture is refused" eligible "$WORK/control/old-observation.json"
observe 'null' > "$WORK/control/regressed.json"
refuse_for "regressed" "a later null last-refresh after a recorded refresh is refused" eligible "$WORK/control/regressed.json"
cp "$WORK/control/now.json" "$WORK/control/now-open.json"; chmod 644 "$WORK/control/now-open.json"
refuse_for "group/other access" "a group/other-readable current input is refused" eligible "$WORK/control/now-open.json"

binding revoked "$EPOCH" > "$WORK/control/binding-revoked.json"
refuse_for "identities differ" "advanced revocation state is refused" eligible "$WORK/control/now.json" "$WORK/control/binding-revoked.json"
binding stopped 33333333-3333-4333-8333-333333333333 > "$WORK/control/binding-epoch.json"
refuse_for "identities differ" "advanced native epoch is refused" eligible "$WORK/control/now.json" "$WORK/control/binding-epoch.json"
binding stopped "$EPOCH" 5e5e5e5e-0000-4000-8000-000000000005 > "$WORK/control/binding-owner.json"
refuse_for "identities differ" "a changed owner generation with the same epoch is refused" eligible "$WORK/control/now.json" "$WORK/control/binding-owner.json"
printf ' ' | cat "$WORK/control/binding.json" - > "$WORK/control/binding-bytes.json"
refuse_for "CAS value differs" "a binding row whose bytes changed is refused" eligible "$WORK/control/now.json" "$WORK/control/binding-bytes.json"
python3 - "$WORK/control/subscription.json" "$WORK/control/subscription-changed.json" <<'PY'
import json, sys
source = json.load(open(sys.argv[1]))
source["expiresAt"] = "2098-01-01T00:00:00Z"  # same identity, a different (changed) outside source
open(sys.argv[2], "w").write(json.dumps(source))
PY
refuse_for "outside source changed" "changed outside source is refused" eligible "$WORK/control/now.json" "$WORK/control/binding.json" "$WORK/control/subscription-changed.json"
printf '{"providerId":"%s"}' "$PROVIDER" > "$WORK/control/refresh-unknown.json"
refuse_for "freshness unknown" "unknown current freshness is refused" eligible "$WORK/control/refresh-unknown.json"
refuse_for "lacks required" "a plain snapshot without control records is not eligible" eligible_at "$plain" "$WORK/control/now.json"
cp -R "$snap" "$WORK/tampered"; printf 'x' >> "$WORK/tampered/control/MANIFEST.json"
refuse_for "hash mismatch" "a snapshot whose hashes no longer match is refused" eligible_at "$WORK/tampered" "$WORK/control/now.json"
cp -R "$snap" "$WORK/empty-sums"; : > "$WORK/empty-sums/SHA256SUMS"
refuse_for "inventory is empty" "an empty checksum inventory is refused" eligible_at "$WORK/empty-sums" "$WORK/control/now.json"
cp -R "$snap" "$WORK/no-ca"; rm "$WORK/no-ca/state/tls/ca.pem"
grep -v '  state/tls/ca.pem$' "$WORK/no-ca/SHA256SUMS" > "$WORK/no-ca/SHA256SUMS.new"; mv "$WORK/no-ca/SHA256SUMS.new" "$WORK/no-ca/SHA256SUMS"
refuse_for "lacks required" "missing gateway state with a consistent inventory is refused" eligible_at "$WORK/no-ca" "$WORK/control/now.json"
cp -R "$snap" "$WORK/no-kek"; rm "$WORK/no-kek/state/credentials/key-encryption-key.bin"
grep -v '  state/credentials/key-encryption-key.bin$' "$WORK/no-kek/SHA256SUMS" > "$WORK/no-kek/SHA256SUMS.new"; mv "$WORK/no-kek/SHA256SUMS.new" "$WORK/no-kek/SHA256SUMS"
refuse_for "configured KEK is not covered" "a snapshot missing the configured KEK and its inventory entry is refused" eligible_at "$WORK/no-kek" "$WORK/control/now.json"
# Python < 3.11 has no tomllib: the same KEK rules hold through the minimal fallback parser.
mkdir -p "$WORK/notoml"; printf 'raise ModuleNotFoundError("tomllib")\n' > "$WORK/notoml/tomllib.py"
PYTHONPATH="$WORK/notoml" expect 0 "fallback parser: subscription backup with an explicit KEK completes" \
  bash "$BACKUP" "$WORK/sub-fallback" "$WORK/gateway-kek.toml" "$state" "${CTL[@]}"
PYTHONPATH="$WORK/notoml" expect 1 "fallback parser: env-only KEK still fails" bash "$BACKUP" "$WORK/env-fallback" "$WORK/gateway-env.toml" "$state" "${CTL[@]}"
PYTHONPATH="$WORK/notoml" refuse_for "configured KEK is not covered" "fallback parser: missing KEK snapshot is refused" eligible_at "$WORK/no-kek" "$WORK/control/now.json"
cp -R "$snap" "$WORK/extra"; printf 'x' > "$WORK/extra/state/unlisted"
refuse_for "does not cover every file" "an unlisted extra file is refused" eligible_at "$WORK/extra" "$WORK/control/now.json"

ln -s "$WORK/control/binding.json" "$WORK/control/binding-link.json"
expect 1 "symlinked control input fails without COMPLETE" bash "$BACKUP" "$WORK/bad1" "$WORK/gateway-kek.toml" "$state" \
  --subscription-source "$WORK/control/subscription.json" --managed-binding "$WORK/control/binding-link.json" --refresh-observation "$WORK/control/refresh.json"
cp "$WORK/control/refresh.json" "$WORK/control/refresh-open.json"; chmod 644 "$WORK/control/refresh-open.json"
expect 1 "group/other-readable control input fails" bash "$BACKUP" "$WORK/bad2" "$WORK/gateway-kek.toml" "$state" \
  --subscription-source "$WORK/control/subscription.json" --managed-binding "$WORK/control/binding.json" --refresh-observation "$WORK/control/refresh-open.json"
printf '{"providerId":"9a9a9a9a-0000-4000-8000-000000000001","ownerGeneration":"%s","statusObservedAtUtc":"2026-10-30T12:00:00Z","providerLastRefreshAtUtc":null,"nextRefreshAtUtc":"2026-10-30T12:35:00Z"}' "$OWNER" > "$WORK/control/refresh-other.json"
chmod 600 "$WORK/control/refresh-other.json"
expect 1 "mismatched refresh observation fails" bash "$BACKUP" "$WORK/bad3" "$WORK/gateway-kek.toml" "$state" \
  --subscription-source "$WORK/control/subscription.json" --managed-binding "$WORK/control/binding.json" --refresh-observation "$WORK/control/refresh-other.json"
expect 1 "partial control flags are a usage error" bash "$BACKUP" "$WORK/bad4" "$WORK/gateway.toml" "$state" --subscription-source "$WORK/control/subscription.json"
for bad in bad1 bad2 bad3; do
  ! ls -d "$WORK/$bad"/gateway-* >/dev/null 2>&1 || fail "$bad left a snapshot"
  [[ -f "$WORK/$bad/last-failure" && ! -f "$WORK/$bad/last-success" ]] || fail "$bad did not stamp last-failure only"
done
ok "failed control inputs leave no COMPLETE snapshot and stamp last-failure"
mkdir -p "$WORK/nomanifest/gateway-20261030T000000Z/control"
: > "$WORK/nomanifest/gateway-20261030T000000Z/COMPLETE"; : > "$WORK/nomanifest/gateway-20261030T000000Z/SHA256SUMS"
date -u +%Y-%m-%dT%H:%M:%SZ > "$WORK/nomanifest/last-success"
expect 1 "freshness refuses a control snapshot without its manifest" bash "$CHECK" "$WORK/nomanifest"

echo "PASS: $pass checks"
