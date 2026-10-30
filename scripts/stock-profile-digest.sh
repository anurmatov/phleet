#!/usr/bin/env bash
# The one content digest of a stock provider profile (#503 W2), the value to pin in a sandbox policy's
# providerProfileHashes and in subscription.json profileSha256:
#
#   openshell [--workspace WS] profile export NAME -o json | scripts/stock-profile-digest.sh
#   scripts/stock-profile-digest.sh EXPORT.json
#
# The pinned CLI renders maps (such as annotations) from an unordered map, so identical exports differ byte for byte
# and a raw-output hash is not a pin. The digest is the lowercase SHA-256 of the canonical UTF-8 form of the FULL
# exported JSON object, every field kept: object keys sorted by code point at every level (duplicates refuse), arrays in
# order, no insignificant whitespace, json.dumps(ensure_ascii=False) string escaping, signed 64-bit integers only (any
# other number, NaN/Infinity, invalid text or nesting deeper than 64 refuses). The orchestrator's StockProfileDigest
# computes the same value; a shared fixture test keeps them equal. Reads at most 1,000,000 bytes; prints only the digest.
set -euo pipefail
[[ $# -le 1 ]] || { echo "usage: $0 [EXPORT.json]  (default: stdin)" >&2; exit 2; }
# The program goes through -c, so the export can arrive on stdin.
program=$(cat <<'PY'
import hashlib, json, sys

LIMIT, MAX_DEPTH = 1_000_000, 64
def refuse(reason):
    sys.exit(f"REFUSED: profile export is not canonicalizable ({reason})")

source = sys.argv[1]
raw = (sys.stdin.buffer if source == "-" else open(source, "rb")).read(LIMIT + 1)
if len(raw) > LIMIT:
    refuse("larger than 1,000,000 bytes")
def pairs(items):
    keys = [k for k, _ in items]
    if len(keys) != len(set(keys)):
        refuse("duplicate key")
    return dict(items)
def integer(text):
    value = int(text)
    if not -2**63 <= value < 2**63:
        refuse("integer outside signed 64-bit")
    return value
def no_float(text):
    refuse("non-integer number")
try:
    document = json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=pairs, parse_int=integer,
                          parse_float=no_float, parse_constant=no_float)
except (UnicodeDecodeError, ValueError) as error:
    refuse(type(error).__name__)
if not isinstance(document, dict):
    refuse("not a JSON object")
def depth_ok(value, depth):
    if isinstance(value, (dict, list)):
        if depth > MAX_DEPTH:
            return False
        children = value.values() if isinstance(value, dict) else value
        return all(depth_ok(child, depth + 1) for child in children)
    return True
if not depth_ok(document, 1):
    refuse("nested deeper than 64")
try:
    canonical = json.dumps(document, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
except UnicodeEncodeError:
    refuse("invalid text")
print(hashlib.sha256(canonical).hexdigest())
PY
)
exec python3 -c "$program" "${1:--}"
