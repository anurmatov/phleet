#!/usr/bin/env bash
# #460 AC-U6: the `codex` branch of entrypoint.sh writes ~/.codex/config.toml as the generated
# /app/codex-root.toml first, then today's [mcp_servers.*] tables.
#
#   bash tests/entrypoint-codex/run.sh
#
# Exit 0: pass, 1: assertion, 2: infrastructure.
#
# Runs the codex branch exactly as it appears in entrypoint.sh, cut out at its `elif` and with every
# `/root/`, `/app/` and `/workspace/` literal rewritten to a scratch directory, so nothing outside it
# is touched. Every generated file is parsed with Python's tomllib: the root keys must sit at the
# document root (a key written after a table header would belong to that table), and the
# [mcp_servers] text must be byte-identical to what the branch wrote before #460 (the GOLDEN_* texts
# below were captured by running that branch on the same fixtures). A missing, unreadable or
# directory codex-root.toml must print one WARN and still write the MCP tables. As root, the branch
# runs as `nobody` through setpriv, so an unreadable file is really unreadable.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
trap 'fail_infra "command failed at line $LINENO"' ERR

ENTRYPOINT="$REPO_ROOT/entrypoint.sh"
[ -f "$ENTRYPOINT" ] || fail_infra "run from a phleet checkout"
command -v node >/dev/null || fail_infra "node is required"
python3 -c 'import tomllib' 2>/dev/null || fail_infra "python3 with tomllib (3.11+) is required"

WORK=$(mktemp -d)
trap 'chmod -R u+rwX "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT
chmod 0755 "$WORK"
ROOT="$WORK/root" APP="$WORK/app" WS="$WORK/workspace"

# The mode defaults HarnessDefaults.Build renders, and one with an extra that changes a table key.
DEFAULT_ROOT='check_for_update_on_startup = false

[analytics]
enabled = false

[history]
persistence = "none"
'
EXTRA_ROOT='check_for_update_on_startup = true

[analytics]
enabled = false

[history]
persistence = "save-all"
'

MCP_JSON='{"mcpServers":{"alpha":{"type":"http","url":"http://alpha:3100?agent=agent1"},"beta":{"type":"http","url":"http://beta:3001"},"gamma":{"type":"http","url":"http://gamma:8083/mcp","headers":{"Authorization":"Bearer placeholder"}},"delta":{"command":"node","args":["x.js"]}}}'
APPSETTINGS='{"Agent":{"AllowedTools":["Read","mcp__alpha__memory_get","mcp__alpha__memory_search","mcp__gamma__send","Bash(git *)"],"McpHeaderSupport":HEADERS}}'

# Captured from the pre-#460 branch on the fixtures above.
GOLDEN_NO_HEADERS='[mcp_servers.alpha]
url = "http://alpha:3100?agent=agent1"
enabled_tools = ["memory_get", "memory_search"]

[mcp_servers.beta]
url = "http://beta:3001"

'
GOLDEN_HEADERS="${GOLDEN_NO_HEADERS}"'[mcp_servers.gamma]
url = "http://gamma:8083/mcp"
http_headers = { "Authorization" = "Bearer placeholder" }
enabled_tools = ["send"]

'

# ── cut the branch and rewrite its paths ─────────────────────────────────────

branch="$WORK/branch.sh" harness="$WORK/harness.sh"
awk '/^elif \[ "\$PROVIDER" = "codex" \]; then$/ {copy=1; next}
     copy && /^(elif|else|fi)([ ;]|$)/ {exit}
     copy' "$ENTRYPOINT" > "$branch"
[ -s "$branch" ] || fail_assert "no codex branch found in entrypoint.sh"
grep -qF '/app/codex-root.toml' "$branch" || fail_assert "the codex branch does not read /app/codex-root.toml"
grep -qF '"/app/codex-root.toml"' "$REPO_ROOT/src/Fleet.Shared/HarnessDefaults.cs" \
    || fail_assert "HarnessDefaults.CodexRootTomlContainerPath is not /app/codex-root.toml"
{
    echo '#!/bin/bash'
    sed -e "s|/root/|$ROOT/|g" -e "s|/app/|$APP/|g" -e "s|/workspace/|$WS/|g" "$branch"
    echo 'echo BRANCH-COMPLETED'
} > "$harness"
! grep -qE '(^|[^A-Za-z0-9_./-])/(root|app|workspace)/' "$harness" || fail_assert "a real path survived the rewrite"
chmod 0755 "$harness"

as_user=()
if [ "$(id -u)" -eq 0 ]; then
    command -v setpriv >/dev/null || fail_infra "running as root needs setpriv to drop to nobody"
    as_user=(setpriv --reuid=65534 --regid=65534 --clear-groups)
fi

# $1 = root file kind (file, missing, directory, unreadable), $2 = root text, $3 = .mcp.json text or
# "" for none, $4 = McpHeaderSupport (true/false).
run_case() {
    chmod -R u+rwX "$ROOT" "$APP" "$WS" 2>/dev/null || true
    rm -rf "$ROOT" "$APP" "$WS"
    mkdir -p "$ROOT" "$APP" "$WS"
    printf '%s' "${APPSETTINGS/HEADERS/$4}" > "$APP/appsettings.json"
    [ -z "$3" ] || printf '%s' "$3" > "$WS/.mcp.json"
    case "$1" in
        file) printf '%s' "$2" > "$APP/codex-root.toml" ;;
        missing) ;;
        directory) mkdir "$APP/codex-root.toml" ;;
        unreadable) printf '%s' "$2" > "$APP/codex-root.toml"; chmod 000 "$APP/codex-root.toml" ;;
    esac
    if [ "$(id -u)" -eq 0 ]; then chown -R 65534:65534 "$ROOT"; fi
    local rc=0
    "${as_user[@]}" env -i PATH="$PATH" HOME="$WORK" LANG=C PROVIDER=codex HOSTED_PROVIDER=false \
        bash "$harness" > "$WORK/out" 2> "$WORK/err" || rc=$?
    [ "$rc" -eq 0 ] || { cat "$WORK/err" >&2; fail_assert "the codex branch exited $rc"; }
    grep -qx BRANCH-COMPLETED "$WORK/out" || fail_assert "the codex branch did not complete"
    CONFIG="$ROOT/.codex/config.toml"
}

# Parses $CONFIG; $1 = expected root text ("" = none), $2 = expected [mcp_servers] text.
assert_config() {
    [ -f "$CONFIG" ] || fail_assert "config.toml was not written"
    python3 - "$CONFIG" "$1" "$2" <<'PY' || fail_assert "config.toml check failed"
import sys, tomllib
path, root, mcp = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(path, encoding="utf-8").read()
doc = tomllib.loads(text)
cut = text.find("[mcp_servers.")
head, tail = (text, "") if cut < 0 else (text[:cut], text[cut:])
assert tail == mcp, f"[mcp_servers] text differs:\n{tail!r}\n!=\n{mcp!r}"
if root:
    expect = tomllib.loads(root)
    assert head == root + ("\n" if mcp else ""), f"root text differs:\n{head!r}"
    for key in ("check_for_update_on_startup", "analytics", "history"):
        assert key in doc and doc[key] == expect[key], f"{key} not at the document root"
    for name, table in doc.get("mcp_servers", {}).items():
        stray = {"check_for_update_on_startup", "analytics", "history", "enabled", "persistence"} & set(table)
        assert not stray, f"root keys leaked into mcp_servers.{name}: {stray}"
else:
    assert head == "", f"unexpected text before the MCP tables: {head!r}"
    assert "check_for_update_on_startup" not in doc and "history" not in doc
PY
}

warn_count() { grep -c "^WARN: codex-root.toml unavailable" "$WORK/err" || true; }

run_case file "$DEFAULT_ROOT" "$MCP_JSON" false
assert_config "$DEFAULT_ROOT" "$GOLDEN_NO_HEADERS"
[ "$(warn_count)" -eq 0 ] || fail_assert "WARN printed with a readable root file"
echo "PASS root + .mcp.json: root keys at the document root, [mcp_servers] byte-identical"

run_case file "$DEFAULT_ROOT" "$MCP_JSON" true
assert_config "$DEFAULT_ROOT" "$GOLDEN_HEADERS"
echo "PASS root + .mcp.json with header support: [mcp_servers] byte-identical"

run_case file "$EXTRA_ROOT" "$MCP_JSON" false
assert_config "$EXTRA_ROOT" "$GOLDEN_NO_HEADERS"
echo "PASS root with an extra: written verbatim, still at the document root"

run_case file "$DEFAULT_ROOT" "" false
assert_config "$DEFAULT_ROOT" ""
echo "PASS root without .mcp.json: config.toml is written and is the root text"

for kind in missing directory unreadable; do
    run_case "$kind" "$DEFAULT_ROOT" "$MCP_JSON" false
    assert_config "" "$GOLDEN_NO_HEADERS"
    [ "$(warn_count)" -eq 1 ] || { cat "$WORK/err" >&2; fail_assert "expected one codex-root.toml WARN ($kind)"; }
    echo "PASS codex-root.toml $kind: one WARN, [mcp_servers] byte-identical"
done

run_case file "$DEFAULT_ROOT" '{not json' false
grep -q '^WARN: codex config.toml not written: ' "$WORK/err" || fail_assert "no WARN when config.toml cannot be built"
[ ! -e "$CONFIG" ] || fail_assert "a partial config.toml was written"
echo "PASS unparseable .mcp.json: one 'config.toml not written' WARN, the branch continues"
