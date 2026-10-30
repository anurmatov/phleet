#!/usr/bin/env bash
# #502 focused, inert rollout/snapshot regressions: no live runtime, gateway, credential or network use.
# Runs on GNU and BSD userlands (bash, python3, mktemp; no sed -i, stat -c or sha256sum).
#
#   bash scripts/test-stock-subscription-rollout.sh
#
# #503 adds: fixed signer-registration CA coverage (both TOML parsers), SNAPSHOT_MODE markers and the four
# control/plain x registered/unregistered eligibility cases with no downgrade, dedicated maintenance S0/S1 retention and
# pre-restart re-verification, and the freshness-only default versus the --require-subscription-recovery warnings.
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
[[ -z "${STUB_FAIL:-}" ]] || exit 22
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
refuse_for "no mode downgrade" "a plain snapshot without control records is not eligible" eligible_at "$plain" "$WORK/control/now.json"
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

# ── #503 H1/F1: fixed signer registration CA coverage, mode markers and both eligibility branches ──
expect_rc() { # expect_rc <rc> <text> <description> <command...>: exactly this exit code and output text
  local want=$1 text=$2 what=$3 rc=0; shift 3
  "$@" >"$WORK/out" 2>&1 || rc=$?
  if [[ $rc -ne $want ]] || ! grep -qF -- "$text" "$WORK/out"; then cat "$WORK/out" >&2; fail "$what (rc=$rc, wanted $want: $text)"; fi
  ok "$what"
}
newest() { ls -d "$1"/gateway-* | sort | tail -n 1; }
strip_entry() { # strip_entry <snapshot> <copy> <entry>: drop one file and its inventory line (a self-consistent reduced list)
  rm -rf "$2"; cp -R "$1" "$2"; rm -rf "${2:?}/$3"
  grep -v "  $3" "$2/SHA256SUMS" > "$2/SHA256SUMS.new" || true; mv "$2/SHA256SUMS.new" "$2/SHA256SUMS"
}
resum() { # resum <snapshot>: rewrite SHA256SUMS over the current files (simulates a self-consistent edited snapshot)
  python3 - "$1" <<'PY'
import hashlib, os, sys
root = sys.argv[1]
lines = []
for base, _, names in os.walk(root):
    for name in sorted(names):
        rel = os.path.relpath(os.path.join(base, name), root)
        if rel not in ("SHA256SUMS", "COMPLETE"):
            lines.append(hashlib.sha256(open(os.path.join(base, name), "rb").read()).hexdigest() + "  " + rel)
open(os.path.join(root, "SHA256SUMS"), "w").write("\n".join(lines) + "\n")
PY
}
remanifest() { # remanifest <snapshot> <copy> <python statement on m>: edit control/MANIFEST.json and resum
  rm -rf "$2"; cp -R "$1" "$2"
  python3 - "$2/control/MANIFEST.json" "$3" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
exec(sys.argv[2])
open(sys.argv[1], "w").write(json.dumps(m))
PY
  resum "$2"
}
plain_eligible() { bash "$CHECK" --plain-restore-eligibility "$1" --state-dir "${2:-$state}"; }
SIGNER_CA="$state/tls/signer/stock-identity-ca.pem"
mkdir -p "$state/tls/signer"; printf 'synthetic signer ca\n' > "$SIGNER_CA"
registration() { # registration [ca-line]: the fixed registration as in deploy/openshell/gateway.toml.example
  printf '\n[[openshell.supervisor.middleware]]\nname = "stock-memory"\ngrpc_endpoint = "https://identity.example.test:5443"\naudience = "synthetic-hook"\n%s\nmax_payload_bytes = 262144\ntimeout = "2s"\nallow_insecure_transport = false\n' "${1:-}"
}
with_ca() { printf 'tls_ca_cert_path = "%s"' "$1"; }
{ cat "$WORK/gateway-kek.toml"; registration "$(with_ca "$SIGNER_CA")"; } > "$WORK/gateway-signer.toml"
{ cat "$WORK/gateway-kek.toml"; registration "$(with_ca "$SIGNER_CA")"; } > "$WORK/gateway-signer-plain.toml"
{ cat "$WORK/gateway.toml"; registration; } > "$WORK/signer-no-ca.toml"
{ cat "$WORK/gateway.toml"; registration "$(with_ca "$state/tls/signer/absent.pem")"; } > "$WORK/signer-absent.toml"
printf 'outside ca\n' > "$WORK/outside-ca.pem"
{ cat "$WORK/gateway.toml"; registration "$(with_ca "$WORK/outside-ca.pem")"; } > "$WORK/signer-outside.toml"
{ cat "$WORK/gateway.toml"; registration "$(with_ca "$state/tls/signer/linked-ca.pem")"; } > "$WORK/signer-linked.toml"
: > "$state/tls/signer/empty-ca.pem"
{ cat "$WORK/gateway.toml"; registration "$(with_ca "$state/tls/signer/empty-ca.pem")"; } > "$WORK/signer-empty.toml"
{ cat "$WORK/gateway-signer-plain.toml"; registration "$(with_ca "$SIGNER_CA")"; } > "$WORK/signer-duplicate.toml"
{ cat "$WORK/gateway.toml"; registration "$(with_ca "$SIGNER_CA")" | sed 's/^\[\[\(.*\)\]\]$/[\1]/'; } > "$WORK/signer-table.toml"
{ cat "$WORK/gateway.toml"; printf '\n[[openshell.supervisor.middleware]]\nname = "other-service"\ngrpc_endpoint = "https://other.example.test:50051"\nmax_payload_bytes = 1024\n'; } > "$WORK/other-middleware.toml"
for parser in tomllib fallback; do
  pp=""; [[ $parser == tomllib ]] || pp="$WORK/notoml"
  for bad in no-ca absent outside empty duplicate table; do
    PYTHONPATH="$pp" refuse_for "coverage_incomplete: signer_tls_ca" "$parser: registration case '$bad' fails even a positional backup" \
      bash "$BACKUP" "$WORK/sig-$parser-$bad" "$WORK/signer-$bad.toml" "$state"
    ! ls -d "$WORK/sig-$parser-$bad"/gateway-* >/dev/null 2>&1 || fail "$parser $bad left a snapshot"
  done
  ln -s "$SIGNER_CA" "$state/tls/signer/linked-ca.pem"
  PYTHONPATH="$pp" refuse_for "coverage_incomplete: signer_tls_ca" "$parser: a linked registration CA fails" bash "$BACKUP" "$WORK/sig-$parser-linked" "$WORK/signer-linked.toml" "$state"
  rm "$state/tls/signer/linked-ca.pem"
  PYTHONPATH="$pp" expect 0 "$parser: another service's registration keeps a config without the fixed one unchanged" bash "$BACKUP" "$WORK/other-$parser" "$WORK/other-middleware.toml" "$state"
done
# The shipped example, rendered for this STATE_DIR with its dormant registration uncommented, parses under both parsers.
python3 - "$ROOT/deploy/openshell/gateway.toml.example" "$state" "$WORK/example.toml" <<'PY'
import re, sys
text = open(sys.argv[1]).read().replace("/var/lib/openshell", sys.argv[2])
text = re.sub(r"^# (\[\[openshell\.supervisor\.middleware\]\]|(name|grpc_endpoint|audience|tls_ca_cert_path|max_payload_bytes|timeout|allow_insecure_transport)\s+=.*)$",
              r"\1", text, flags=re.M)
assert text.count('name                     = "stock-memory"') == 1 and "\n[[openshell.supervisor.middleware]]\n" in text
open(sys.argv[3], "w").write(text)
PY
cp "$SIGNER_CA" "$state/tls/stock-identity-ca.pem"
expect 0 "tomllib: the rendered example with its registration covers the CA" bash "$BACKUP" "$WORK/example-tomllib" "$WORK/example.toml" "$state"
PYTHONPATH="$WORK/notoml" expect 0 "fallback: the rendered example (array tables, supervisor_image) parses the same" bash "$BACKUP" "$WORK/example-fallback" "$WORK/example.toml" "$state"
for parser in tomllib fallback; do
  grep -q "  state/tls/stock-identity-ca.pem$" "$(newest "$WORK/example-$parser")/SHA256SUMS" || fail "$parser example snapshot lacks the signer CA"
done
rm "$state/tls/stock-identity-ca.pem"
ok "both parsers cover the example registration CA in the inventory"

# C13 case a: control-bearing S0 without a registration records explicit-null signer metadata and the mode marker.
python3 - "$snap" "$state" <<'PY' || fail "S0 manifest signer metadata"
import json, os, sys
m = json.load(open(os.path.join(sys.argv[1], "control", "MANIFEST.json")))
assert all(k in m and m[k] is None for k in ("signerRegistration", "signerCaConfigPath", "signerCaEntry"))
assert m["stateDirectory"] == os.path.join(os.path.realpath(os.path.dirname(sys.argv[2])), os.path.basename(sys.argv[2]))
assert open(os.path.join(sys.argv[1], "SNAPSHOT_MODE"), "rb").read() == b"subscription\n"
assert "  SNAPSHOT_MODE" in open(os.path.join(sys.argv[1], "SHA256SUMS")).read()
PY
ok "control S0 without registration: explicit-null signer fields, subscription marker in the inventory"
[[ "$(cat "$plain/SNAPSHOT_MODE")" == plain ]] || fail "positional snapshot is not marked plain"
# C13 case b: control-bearing S1 with the registration.
expect 0 "control S1 with the registration completes" bash "$BACKUP" "$WORK/s1" "$WORK/gateway-signer.toml" "$state" "${CTL[@]}"
s1=$(newest "$WORK/s1")
python3 - "$s1" "$SIGNER_CA" <<'PY' || fail "S1 manifest signer metadata"
import json, os, sys
m = json.load(open(os.path.join(sys.argv[1], "control", "MANIFEST.json")))
assert (m["signerRegistration"], m["signerCaConfigPath"], m["signerCaEntry"]) == ("stock-memory", sys.argv[2], "state/tls/signer/stock-identity-ca.pem")
assert "  state/tls/signer/stock-identity-ca.pem" in open(os.path.join(sys.argv[1], "SHA256SUMS")).read()
PY
ok "control S1 records the registration, CA path and its contained inventory entry"
observe '"2000-01-01T00:00:00Z"' > "$WORK/control/after-s1.json"
expect 0 "control S1/registered CA is eligible" eligible_at "$s1" "$WORK/control/after-s1.json"
expect 0 "control S0/no registration is still eligible after S1" eligible_at "$snap" "$WORK/control/after-s1.json"
# C13 cases c and d: plain snapshots with a live zero-custody preflight.
# B1: the plain branch restores only an explicitly configured KEK it can find in the inventory.
expect 0 "positional backup with an explicit KEK path" bash "$BACKUP" "$WORK/plain-kek" "$WORK/gateway-kek.toml" "$state"
plainkek=$(newest "$WORK/plain-kek")
STUB_BODY=$(four 0 0 0 0) expect 0 "plain/no registration is eligible with live zero custody" plain_eligible "$plainkek"
STUB_BODY=$(four 0 0 0 0) refuse_for "configured KEK is not covered" "plain refuses a default-KEK snapshot (no key in the inventory)" plain_eligible "$plain"
expect 0 "legacy positional backup with an env-only KEK is unchanged" bash "$BACKUP" "$WORK/plain-env" "$WORK/gateway-env.toml" "$state"
STUB_BODY=$(four 0 0 0 0) refuse_for "configured KEK is not covered" "plain refuses an env-only-KEK snapshot (no key in the inventory)" plain_eligible "$(newest "$WORK/plain-env")"
strip_entry "$plainkek" "$WORK/plain-nokek" state/credentials/key-encryption-key.bin
STUB_BODY=$(four 0 0 0 0) refuse_for "configured KEK is not covered" "plain refuses a KEK path plus checksum line removed" plain_eligible "$WORK/plain-nokek"
expect 0 "positional backup with the registration covers its CA" bash "$BACKUP" "$WORK/plain-sig" "$WORK/gateway-signer-plain.toml" "$state"
plainsig=$(newest "$WORK/plain-sig")
STUB_BODY=$(four 0 0 0 0) expect 0 "plain/registered CA is eligible with live zero custody" plain_eligible "$plainsig"
PYTHONPATH="$WORK/notoml" STUB_BODY=$(four 0 0 0 0) expect 0 "fallback parser: plain/registered CA is eligible" plain_eligible "$plainsig"
# CA plus its checksum line removed (self-consistent reduced inventory) never restores.
strip_entry "$plainsig" "$WORK/plain-noca" state/tls/signer/stock-identity-ca.pem
STUB_BODY=$(four 0 0 0 0) refuse_for "signer_tls_ca_not_covered" "plain: CA plus its checksum line removed is refused" plain_eligible "$WORK/plain-noca"
PYTHONPATH="$WORK/notoml" STUB_BODY=$(four 0 0 0 0) refuse_for "signer_tls_ca_not_covered" "fallback parser: plain CA removal is refused" plain_eligible "$WORK/plain-noca"
strip_entry "$s1" "$WORK/s1-noca" state/tls/signer/stock-identity-ca.pem
refuse_for "signer_tls_ca_not_covered" "control: CA plus its checksum line removed is refused" eligible_at "$WORK/s1-noca" "$WORK/control/after-s1.json"
STUB_BODY=$(four 0 0 0 0) refuse_for "configured KEK is not covered" "plain: a STATE_DIR the KEK and CA are not under is refused" plain_eligible "$plainsig" "$WORK/elsewhere"
# Wrong or null signer metadata in a control manifest.
remanifest "$s1" "$WORK/s1-null" 'm["signerCaEntry"] = None'
refuse_for "signer metadata does not match" "control S1 with a null CA entry is refused" eligible_at "$WORK/s1-null" "$WORK/control/after-s1.json"
remanifest "$s1" "$WORK/s1-path" 'm["signerCaConfigPath"] = "/elsewhere/ca.pem"'
refuse_for "signer metadata does not match" "control S1 with a different CA path is refused" eligible_at "$WORK/s1-path" "$WORK/control/after-s1.json"
remanifest "$snap" "$WORK/s0-claims" 'm["signerRegistration"] = "stock-memory"'
refuse_for "signer metadata does not match" "control S0 claiming a registration its config lacks is refused" eligible_at "$WORK/s0-claims" "$WORK/control/after-s1.json"
remanifest "$snap" "$WORK/s0-missing" 'del m["signerCaEntry"]'
refuse_for "lacks the recorded STATE_DIR or signer metadata" "control S0 with a missing (not null) signer field is refused" eligible_at "$WORK/s0-missing" "$WORK/control/after-s1.json"

# CA1: a linked component below STATE_DIR (tls/alias -> tls/signer) never stands in for the configured CA path.
ln -s signer "$state/tls/alias"
ALIAS_CA="$state/tls/alias/stock-identity-ca.pem"
{ cat "$WORK/gateway-kek.toml"; registration "$(with_ca "$ALIAS_CA")"; } > "$WORK/signer-alias.toml"
for parser in tomllib fallback; do
  pp=""; [[ $parser == tomllib ]] || pp="$WORK/notoml"
  PYTHONPATH="$pp" refuse_for "coverage_incomplete: signer_tls_ca" "$parser: a CA under a linked tls/alias directory fails a positional backup" \
    bash "$BACKUP" "$WORK/alias-$parser" "$WORK/signer-alias.toml" "$state"
  PYTHONPATH="$pp" refuse_for "coverage_incomplete: signer_tls_ca" "$parser: a CA under a linked tls/alias directory fails a subscription backup" \
    bash "$BACKUP" "$WORK/alias-sub-$parser" "$WORK/signer-alias.toml" "$state" "${CTL[@]}"
  ! ls -d "$WORK/alias-$parser"/gateway-* "$WORK/alias-sub-$parser"/gateway-* >/dev/null 2>&1 || fail "$parser alias backup left a snapshot"
done
with_config() { # with_config <snapshot> <copy> <config>: the snapshot with its config replaced, inventory rewritten
  rm -rf "$2"; cp -R "$1" "$2"; cp "$3" "$2/config/$(ls "$2/config")"; resum "$2"
}
with_config "$plainsig" "$WORK/plain-alias" "$WORK/signer-alias.toml"
for parser in tomllib fallback; do
  pp=""; [[ $parser == tomllib ]] || pp="$WORK/notoml"
  PYTHONPATH="$pp" STUB_BODY=$(four 0 0 0 0) refuse_for "signer_tls_ca_not_covered" \
    "$parser: plain refuses a config naming tls/alias whose inventory holds only tls/signer" plain_eligible "$WORK/plain-alias"
done
with_config "$s1" "$WORK/s1-alias" "$WORK/signer-alias.toml"
remanifest "$WORK/s1-alias" "$WORK/s1-alias-meta" "m['signerCaConfigPath'] = '$ALIAS_CA'"
refuse_for "signer_tls_ca_not_covered" "control refuses a config naming tls/alias whose inventory holds only tls/signer" eligible_at "$WORK/s1-alias-meta" "$WORK/control/after-s1.json"
rm "$state/tls/alias"
# P1: tomllib reads quoted/escaped keys as the registration; the fallback must refuse, never skip, what it cannot parse.
printf '\n[[openshell."super\\u0076isor"."middle\\u0077are"]]\nname = "stock-memory"\n' > "$WORK/escaped-registration.part"
# After an unrelated table, as an operator config would have it, so the header is not caught by accident.
{ cat "$WORK/gateway-kek.toml"; printf '\n[openshell.drivers.docker]\nimage_pull_policy = "if_not_present"\n'
  cat "$WORK/escaped-registration.part"; } > "$WORK/signer-escaped.toml"
printf '[openshell]\nversion = 2\n"super\\u0076isor"."middle\\u0077are" = [{ name = "stock-memory" }]\n\n[openshell.gateway.credential_storage]\nkey_encryption_key_path = "%s"\n' \
  "$state/credentials/key-encryption-key.bin" > "$WORK/signer-escaped-key.toml"
python3 - "$WORK/signer-escaped.toml" "$WORK/signer-escaped-key.toml" <<'PY' || fail "tomllib does not read the escaped forms as the registration"
import sys, tomllib
for path in sys.argv[1:]:
    assert tomllib.loads(open(path).read())["openshell"]["supervisor"]["middleware"] == [{"name": "stock-memory"}], path
PY
with_config "$plainkek" "$WORK/plain-escaped" "$WORK/signer-escaped.toml"
with_config "$snap" "$WORK/s0-escaped" "$WORK/signer-escaped.toml"
for parser in tomllib fallback; do
  pp=""; [[ $parser == tomllib ]] || pp="$WORK/notoml"
  for config in escaped escaped-key; do
    PYTHONPATH="$pp" refuse_for "coverage_incomplete: signer_tls_ca" "$parser: $config registration without a CA fails a positional backup" \
      bash "$BACKUP" "$WORK/p1-$parser-$config" "$WORK/signer-$config.toml" "$state"
  done
  # tomllib reads the registration and finds no CA; the fallback cannot read the config at all, so it refuses at the
  # first config-dependent check (the KEK) instead. Either way the snapshot is never eligible.
  reason=signer_tls_ca_not_covered; [[ $parser == tomllib ]] || reason="configured KEK is not covered"
  PYTHONPATH="$pp" STUB_BODY=$(four 0 0 0 0) refuse_for "$reason" "$parser: plain refuses the escaped registration without a CA" \
    plain_eligible "$WORK/plain-escaped"
  PYTHONPATH="$pp" refuse_for "$reason" "$parser: control refuses the escaped registration without a CA" \
    eligible_at "$WORK/s0-escaped" "$WORK/control/after-s1.json"
done

# P1: an inline-table assignment to an ancestor of the relevant tables is the registration too (tomllib), never absence.
printf 'openshell = { version = 2, supervisor = { middleware = [{ name = "stock-memory" }] } }\n' > "$WORK/signer-root-inline.toml"
printf '[openshell]\nversion = 2\nsupervisor = { middleware = [{ name = "stock-memory" }] }\n' > "$WORK/signer-table-inline.toml"
printf 'openshell = { version = 2, gateway = { credential_storage = { key_encryption_key_path = "%s" } }, supervisor = { middleware = [{ name = "stock-memory" }] } }\n' \
  "$state/credentials/key-encryption-key.bin" > "$WORK/signer-root-inline-kek.toml"
python3 - "$WORK"/signer-root-inline.toml "$WORK"/signer-table-inline.toml "$WORK"/signer-root-inline-kek.toml <<'PY' || fail "tomllib does not read the inline forms as the registration"
import sys, tomllib
for path in sys.argv[1:]:
    assert tomllib.loads(open(path).read())["openshell"]["supervisor"]["middleware"] == [{"name": "stock-memory"}], path
PY
with_config "$plainkek" "$WORK/plain-inline" "$WORK/signer-root-inline-kek.toml"
with_config "$snap" "$WORK/s0-inline" "$WORK/signer-root-inline-kek.toml"
for parser in tomllib fallback; do
  pp=""; [[ $parser == tomllib ]] || pp="$WORK/notoml"
  for config in root-inline table-inline root-inline-kek; do
    PYTHONPATH="$pp" refuse_for "coverage_incomplete: signer_tls_ca" "$parser: $config registration without a CA fails a positional backup" \
      bash "$BACKUP" "$WORK/inline-$parser-$config" "$WORK/signer-$config.toml" "$state"
    ! ls -d "$WORK/inline-$parser-$config"/gateway-* >/dev/null 2>&1 || fail "$parser $config left a snapshot"
  done
  # The fallback also cannot read this config's KEK, so its coverage list names that before signer_tls_ca.
  PYTHONPATH="$pp" refuse_for "signer_tls_ca" "$parser: root inline registration without a CA fails a subscription backup" \
    bash "$BACKUP" "$WORK/inline-sub-$parser" "$WORK/signer-root-inline-kek.toml" "$state" "${CTL[@]}"
  reason=signer_tls_ca_not_covered; [[ $parser == tomllib ]] || reason="configured KEK is not covered"
  PYTHONPATH="$pp" STUB_BODY=$(four 0 0 0 0) refuse_for "$reason" "$parser: plain refuses the root inline registration without a CA" \
    plain_eligible "$WORK/plain-inline"
  PYTHONPATH="$pp" refuse_for "$reason" "$parser: control refuses the root inline registration without a CA" \
    eligible_at "$WORK/s0-inline" "$WORK/control/after-s1.json"
done
# Unrelated inline values keep working in the fallback: only relevant paths fail closed.
{ cat "$WORK/gateway-kek.toml"; printf '\n[openshell.gateway]\nextra = { level = "info" }\n'; } > "$WORK/unrelated-inline.toml"
for parser in tomllib fallback; do
  pp=""; [[ $parser == tomllib ]] || pp="$WORK/notoml"
  PYTHONPATH="$pp" expect 0 "$parser: an unrelated inline table keeps a config without the registration backing up" \
    bash "$BACKUP" "$WORK/unrelated-$parser" "$WORK/unrelated-inline.toml" "$state"
done

# C13a: custody, marker and downgrade negatives for the plain branch; no restore or start side effect is possible here.
STUB_BODY=$(four 0 1 0 1) refuse_for "subscription_state_unverified" "plain refuses while subscriptionBindings>0" plain_eligible "$plainkek"
STUB_BODY=$(four 0 1 1 0) refuse_for "subscription_state_unverified" "plain refuses while unresolvedBindings>0" plain_eligible "$plainkek"
STUB_BODY='{"desiredOpenShell":0,"appliedOpenShell":0,"unresolvedBindings":0}' refuse_for "subscription_state_unverified" "plain refuses a malformed preflight answer" plain_eligible "$plainkek"
STUB_BODY='not json' refuse_for "subscription_state_unverified" "plain refuses an unparsable preflight answer" plain_eligible "$plainkek"
STUB_FAIL=1 STUB_BODY=$(four 0 0 0 0) refuse_for "subscription_state_unverified" "plain refuses an unavailable or timed-out preflight" plain_eligible "$plainkek"
rm -f "$STUB_CALLS"
ORCHESTRATOR_AUTH_TOKEN= STUB_BODY=$(four 0 0 0 0) refuse_for "subscription_state_unverified" "plain refuses without an operator token" plain_eligible "$plainkek"
[[ ! -e "$STUB_CALLS" ]] || fail "plain eligibility queried without a token"
STUB_BODY=$(four 0 0 0 0) refuse_for "not plain" "a subscription-marked snapshot is refused by the plain branch" plain_eligible "$snap"
strip_entry "$snap" "$WORK/stripped" control
STUB_BODY=$(four 0 0 0 0) refuse_for "not plain" "a subscription snapshot with controls and their checksum lines stripped is still refused" plain_eligible "$WORK/stripped"
strip_entry "$plainkek" "$WORK/unmarked" SNAPSHOT_MODE
STUB_BODY=$(four 0 0 0 0) refuse_for "marker missing or unknown" "plain: a missing mode marker is refused" plain_eligible "$WORK/unmarked"
strip_entry "$snap" "$WORK/unmarked-sub" SNAPSHOT_MODE
refuse_for "marker missing or unknown" "control: a missing mode marker is refused" eligible_at "$WORK/unmarked-sub" "$WORK/control/after-s1.json"
rm -rf "$WORK/other-marker"; cp -R "$plainkek" "$WORK/other-marker"; printf 'mixed\n' > "$WORK/other-marker/SNAPSHOT_MODE"; resum "$WORK/other-marker"
STUB_BODY=$(four 0 0 0 0) refuse_for "marker missing or unknown" "an unknown mode marker is refused" plain_eligible "$WORK/other-marker"
rm -rf "$WORK/upgraded"; cp -R "$plain" "$WORK/upgraded"; printf 'subscription\n' > "$WORK/upgraded/SNAPSHOT_MODE"; resum "$WORK/upgraded"
refuse_for "lacks required" "a plain snapshot re-marked subscription has no control payload and is refused" eligible_at "$WORK/upgraded" "$WORK/control/after-s1.json"
rm -rf "$WORK/downgraded"; cp -R "$snap" "$WORK/downgraded"; printf 'plain\n' > "$WORK/downgraded/SNAPSHOT_MODE"; resum "$WORK/downgraded"
STUB_BODY=$(four 0 0 0 0) refuse_for "refuses a control-bearing snapshot" "a control snapshot re-marked plain is refused by the plain branch" plain_eligible "$WORK/downgraded"
refuse_for "not subscription" "the re-marked control snapshot is refused by the control branch too (no downgrade)" eligible_at "$WORK/downgraded" "$WORK/control/after-s1.json"

# ── #503 N1 (C16): once-used maintenance S0/S1 directories outside daily rotation, pre-restart re-verification ──
daily="$WORK/daily"; maint="$WORK/maintenance/window-1"
mkdir -p "$daily"
for day in 1 2 3 4 5 6 7 8 9; do mkdir -p "$daily/gateway-2026010${day}T000000Z"; : > "$daily/gateway-2026010${day}T000000Z/COMPLETE"; done
[[ ! -e "$maint" ]] || fail "maintenance window directory pre-exists"
expect 0 "maintenance S0 goes to its own initially absent directory" bash "$BACKUP" "$maint/S0" "$WORK/gateway-kek.toml" "$state" "${CTL[@]}"
expect 0 "maintenance S1 goes to its own initially absent directory" bash "$BACKUP" "$maint/S1" "$WORK/gateway-signer.toml" "$state" "${CTL[@]}"
tree_digest() { python3 - "$1" <<'PY'
import hashlib, os, sys
h = hashlib.sha256()
for base, dirs, names in sorted(os.walk(sys.argv[1])):
    for name in sorted(names):
        path = os.path.join(base, name)
        h.update(os.path.relpath(path, sys.argv[1]).encode() + b"\0" + open(path, "rb").read() + b"\0")
print(h.hexdigest())
PY
}
before=$(tree_digest "$WORK/maintenance")
expect 0 "a daily positional run prunes inside its own directory" bash "$BACKUP" "$daily" "$WORK/gateway.toml" "$state"
[[ $(ls -d "$daily"/gateway-* | wc -l | tr -d ' ') -eq 7 ]] || fail "daily directory did not keep exactly seven snapshots"
[[ "$(tree_digest "$WORK/maintenance")" == "$before" ]] || fail "daily pruning touched the maintenance checkpoints"
ok "daily pruning beyond seven leaves S0/S1 paths and bytes unchanged"
pre_restart() { # pre_restart <S0 snapshot> <S1 snapshot> <observation>: BOTH must be currently eligible right before restart
  eligible_at "$1" "$3" && eligible_at "$2" "$3"
}
S0=$(newest "$maint/S0"); S1=$(newest "$maint/S1")
observe '"2000-01-01T00:00:00Z"' > "$WORK/control/pre-restart.json"
expect 0 "both current S0 and S1 pass the pre-restart re-verification" pre_restart "$S0" "$S1" "$WORK/control/pre-restart.json"
refuse_for "snapshot is not complete" "a missing S0 forbids restart even with a valid S1" pre_restart "$maint/S0/gateway-absent" "$S1" "$WORK/control/pre-restart.json"
rm -rf "$WORK/s0-corrupt"; cp -R "$S0" "$WORK/s0-corrupt"; printf 'x' >> "$WORK/s0-corrupt/gateway.db"
refuse_for "hash mismatch" "a corrupt S0 forbids restart even with a valid S1" pre_restart "$WORK/s0-corrupt" "$S1" "$WORK/control/pre-restart.json"
# A provider refresh between S0 and S1 makes S0 stale while a later S1 stays eligible: restart is still forbidden.
refreshed=$(now_utc)
refresh "\"$refreshed\"" > "$WORK/control/refresh-between.json"; chmod 600 "$WORK/control/refresh-between.json"
expect 0 "a later S1 after a provider refresh" bash "$BACKUP" "$WORK/maintenance/window-2/S1" "$WORK/gateway-signer.toml" "$state" \
  --subscription-source "$WORK/control/subscription.json" --managed-binding "$WORK/control/binding.json" --refresh-observation "$WORK/control/refresh-between.json"
observe "\"$refreshed\"" > "$WORK/control/after-refresh.json"
expect 0 "the later S1 is currently eligible" eligible_at "$(newest "$WORK/maintenance/window-2/S1")" "$WORK/control/after-refresh.json"
refuse_for "superseded" "a stale S0 forbids restart even with a currently valid S1" pre_restart "$S0" "$(newest "$WORK/maintenance/window-2/S1")" "$WORK/control/after-refresh.json"

# ── #503 N2 (C17): freshness-only default and the subscription-recovery warning mode ──
expect_rc 0 "freshness only; restore eligibility not assessed" "the no-flag check is explicitly freshness-only" bash "$CHECK" "$WORK/plain"
expect_rc 3 "WARN: subscription_recovery_unqualified" "fresh positional/plain newest snapshot warns unqualified (exit 3)" bash "$CHECK" --require-subscription-recovery "$WORK/plain"
expect_rc 3 "WARN: subscription_recovery_unqualified" "a fresh daily directory warns unqualified (exit 3)" bash "$CHECK" --require-subscription-recovery "$daily" 26
expect_rc 3 "WARN: subscription_recovery_unverified" "a fresh control-bearing newest snapshot warns unverified (exit 3)" bash "$CHECK" --require-subscription-recovery "$WORK/sub"
rm -rf "$WORK/legacy-daily"; cp -R "$WORK/plain" "$WORK/legacy-daily"; cp -R "$plain" "$WORK/legacy-daily/gateway-29990101T000000Z"
rm "$WORK/legacy-daily/gateway-29990101T000000Z/SNAPSHOT_MODE"
expect_rc 3 "WARN: subscription_recovery_unqualified" "an unmarked newest snapshot warns unqualified (exit 3)" bash "$CHECK" --require-subscription-recovery "$WORK/legacy-daily"
rm -rf "$WORK/stale-daily"; cp -R "$WORK/sub" "$WORK/stale-daily"; printf '2000-01-01T00:00:00Z\n' > "$WORK/stale-daily/last-success"
expect_rc 1 "STALE" "a stale backup stays exit 1 with the flag" bash "$CHECK" --require-subscription-recovery "$WORK/stale-daily"
rm -rf "$WORK/failed-daily"; cp -R "$WORK/sub" "$WORK/failed-daily"; cp "$WORK/failed-daily/last-success" "$WORK/failed-daily/last-failure"
expect_rc 1 "STALE" "a failed latest run stays exit 1 with the flag" bash "$CHECK" --require-subscription-recovery "$WORK/failed-daily"

echo "PASS: $pass checks"
