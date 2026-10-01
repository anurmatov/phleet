#!/usr/bin/env bash
# Secret-free #395 regression. Exit 0: pass, 1: assertion, 2: infrastructure.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
trap 'fail_infra "command failed at line $LINENO"' ERR
for tool in awk sed grep wc git cmp mktemp; do
    command -v "$tool" >/dev/null || fail_infra "missing $tool"
done
WORK=$(mktemp -d)
trap 'chmod -R u+rwX "$WORK"; rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin" "$WORK/outside" "$WORK/home"
export HOME="$WORK/home" GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
export GIT_CEILING_DIRECTORIES="$WORK" GIT_TERMINAL_PROMPT=0
# Ignore caller repository overrides; all git operations target scratch checkouts.
unset GIT_DIR GIT_WORK_TREE GIT_COMMON_DIR GIT_INDEX_FILE GIT_CONFIG_COUNT
if [ -n "${WRAPPER:-}" ]; then
    SOURCE="$WRAPPER"
    [ -f "$SOURCE" ] || SOURCE="$REPO_ROOT/$WRAPPER"
    [ -f "$SOURCE" ] || fail_infra "wrapper fixture not found"
    cp "$SOURCE" "$WORK/source"
else
    awk '/<< '\''GHWRAPPER'\''/{copy=1;next} /^GHWRAPPER$/{copy=0} copy' \
        "$REPO_ROOT/gh-auth.sh" > "$WORK/source"
fi
[ -s "$WORK/source" ] || fail_assert "wrapper extraction is empty"
# No token file is accessed until every literal path has been rewritten.
LITERALS=$( { grep -oF '/tmp/.github-token-' "$WORK/source" || true; } | wc -l)
[ "$LITERALS" -gt 0 ] || fail_assert "no token paths found"
sed "s|/tmp/.github-token-|$WORK/.github-token-|g" "$WORK/source" > "$WORK/bin/gh"
REWRITTEN=$( { grep -oF "$WORK/.github-token-" "$WORK/bin/gh" || true; } | wc -l)
[ "$LITERALS" -eq "$REWRITTEN" ] || fail_assert "token rewrite count mismatch"
REMAINING=$(grep -cF '/tmp/.github-token-' "$WORK/bin/gh" || true)
[ "$REMAINING" -eq 0 ] || fail_assert "real token path survived rewrite"
echo "PASS isolation: rewrote $REWRITTEN paths, real-path count=$REMAINING"
cat > "$WORK/bin/gh-real" <<'FAKE'
#!/bin/bash
printf 'SELECTED=%s\n' "$GH_TOKEN"
if [ "$#" -gt 0 ]; then
    printf '%s\0' "$@" > "$ARGV_CAPTURE"
else
    : > "$ARGV_CAPTURE"
fi
FAKE
chmod +x "$WORK/bin/gh" "$WORK/bin/gh-real"
for owner in owner-a owner-b primary; do
    printf 'sentinel-%s' "$owner" > "$WORK/.github-token-$owner"
done
# Invalid owners have scratch token files too: validation, not a missing file,
# must prevent their selection. These names cannot escape the scratch directory.
for owner in 'a b' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa owner.x '{owner}' . .. _; do
    printf 'sentinel-owner-b' > "$WORK/.github-token-$owner"
done
for owner in owner-a owner-b; do
    git init -q "$WORK/$owner"
    git -C "$WORK/$owner" remote add origin "https://github.com/$owner/repo.git"
done
COUNT=0
check() {
    local label="$1" cwd="$2" expected="$3"
    shift 3
    if [ "$#" -gt 0 ]; then
        printf '%s\0' "$@" > "$WORK/expected-argv"
    else
        : > "$WORK/expected-argv"
    fi
    (cd "$WORK/$cwd"; ARGV_CAPTURE="$WORK/argv" "$WORK/bin/gh" "$@") > "$WORK/selected"
    printf 'SELECTED=%s\n' "$expected" > "$WORK/expected-selected"
    cmp -s "$WORK/expected-selected" "$WORK/selected" || fail_assert "$label: selected token"
    cmp -s "$WORK/expected-argv" "$WORK/argv" || fail_assert "$label: argv changed"
    COUNT=$((COUNT + 1))
    # Only known sentinels reach logs; empty GH_TOKEN has no SELECTED log line.
    [ -z "$expected" ] || cat "$WORK/selected"
    echo "PASS $label (argv byte-equal)"
}
check 'AC1 row 1' outside sentinel-owner-b api -X POST repos/owner-b/repo-b/issues/1/comments --input -
check 'leading slash' outside sentinel-owner-b api /repos/owner-b/repo-b/pulls
check 'URL query' outside sentinel-owner-b api 'https://api.github.com/repos/owner-b/repo-b/pulls?per_page=5'
check 'multiple flags' outside sentinel-owner-b api -H 'Accept: x' --method GET --jq .x repos/owner-b/r
check 'attached short' outside sentinel-owner-b api -XPOST repos/owner-b/r/issues
check 'separator' outside sentinel-owner-b api --paginate -- repos/owner-b/r/issues
check 'input is not endpoint' outside sentinel-owner-a api --input repos/owner-b/body.json repos/owner-a/r/issues
check 'endpoint beats cwd' owner-a sentinel-owner-b api repos/owner-b/repo-b/issues
check 'placeholder uses cwd' owner-a sentinel-owner-a api 'repos/{owner}/{repo}/pulls'
check 'cluster separate value' outside sentinel-owner-b api -iX GET repos/owner-b/r
check 'cluster attached value' outside sentinel-owner-b api -iXGET repos/owner-b/r
check 'template outside' outside sentinel-primary api -it 'repos/owner-b/r' user
check 'template inside' owner-a sentinel-owner-a api -it 'repos/owner-b/r' user
check 'long equals' outside sentinel-owner-b api --method=POST repos/owner-b/r/issues
check 'unknown short' outside sentinel-primary api -Z repos/owner-b/r
check 'unknown long' outside sentinel-primary api --no-such-flag repos/owner-b/r
check 'missing owner token' outside sentinel-primary api repos/owner-c/r/issues
# A valid maximum-length login still routes; the 40-character fixture below must not.
MAX_OWNER=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
printf 'sentinel-owner-b' > "$WORK/.github-token-$MAX_OWNER"
check '39-character owner' outside sentinel-owner-b api "repos/$MAX_OWNER/r"
for owner in 'a b' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa owner.x . .. _; do
    check 'invalid owner' outside sentinel-primary api "repos/$owner/r"
done
check 'repo flag' outside sentinel-owner-b pr list --repo owner-b/r
check 'short repo flag' outside sentinel-owner-b issue view 1 -R owner-b/r
check 'cwd fallback' owner-a sentinel-owner-a pr list
check 'primary fallback' outside sentinel-primary pr list
check 'graphql outside' outside sentinel-primary api graphql -f query=q
check 'graphql inside' owner-b sentinel-owner-b api graphql -f query=q
check 'user' outside sentinel-primary api user
# Exercise every flag table entry, including values shaped like endpoints.
for flag in X H f F q t p; do
    check "short $flag separate" outside sentinel-owner-a api "-$flag" repos/owner-b/value repos/owner-a/r
    check "short $flag attached" outside sentinel-owner-a api "-${flag}repos/owner-b/value" repos/owner-a/r
done
for flag in method header raw-field field jq template preview input hostname cache; do
    check "long $flag separate" outside sentinel-owner-a api "--$flag" repos/owner-b/value repos/owner-a/r
    check "long $flag equals" outside sentinel-owner-a api "--$flag=repos/owner-b/value" repos/owner-a/r
done
for flag in include paginate silent slurp verbose allow-escape-sequences help; do
    check "boolean $flag" outside sentinel-owner-b api "--$flag" repos/owner-b/r
    check "boolean $flag equals" outside sentinel-owner-b api "--$flag=false" repos/owner-b/r
done
check 'boolean short cluster' outside sentinel-owner-b api -ih repos/owner-b/r
check 'unknown cluster tail' outside sentinel-primary api -iZ repos/owner-b/r
check 'unknown flag uses cwd' owner-a sentinel-owner-a api --unknown repos/owner-b/r
check 'repo beats endpoint' owner-a sentinel-owner-a api repos/owner-b/r --repo owner-a/r
check 'repo equals unchanged' outside sentinel-primary pr list --repo=owner-b/r
check 'org unchanged' owner-a sentinel-owner-a api orgs/owner-b/repos
check 'installation unchanged' outside sentinel-primary api installation/repositories
check 'other host unchanged' outside sentinel-primary api https://uploads.github.com/repos/owner-b/r
check 'GHES unchanged' outside sentinel-primary api https://example.com/repos/owner-b/r
check 'missing repo component' outside sentinel-primary api repos/owner-b/
check 'query without repo' outside sentinel-primary api 'repos/owner-b/?x=1'
check 'no arguments' outside sentinel-primary
check 'no endpoint' outside sentinel-primary api --method GET
check 'missing flag value' outside sentinel-primary api -X
check 'lone dash endpoint' outside sentinel-primary api - repos/owner-b/r
check 'separator ends scan' outside sentinel-primary api -- --method repos/owner-b/r
check 'newline argument' outside sentinel-owner-b api repos/owner-b/r -f $'body=first\nsecond' -f ''
if [ "$EUID" -eq 0 ]; then
    echo 'SKIP unreadable api (requires non-root)'
    echo 'SKIP unreadable --repo (requires non-root)'
else
    chmod 000 "$WORK/.github-token-owner-b"
    check 'unreadable api' outside '' api repos/owner-b/r
    check 'unreadable --repo' outside '' pr list --repo owner-b/r
    chmod 600 "$WORK/.github-token-owner-b"
fi
rm "$WORK/.github-token-primary"
check 'missing primary' outside '' pr list
check 'missing primary api' outside '' api user
echo "PASS $COUNT routing and argv cases"
