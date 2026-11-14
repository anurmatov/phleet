#!/usr/bin/env bash
# entrypoint.sh no longer reads appsettings.json itself: `dotnet Fleet.Agent.dll prepare` does, and
# writes the provider CLI configuration the script's inline Node and Python used to write. This
# harness proves three things about that move.
#
#   bash tests/entrypoint-prepare/run.sh        (needs a built agent: dotnet build src/Fleet.Agent -c Release)
#
# Exit 0: pass, 1: assertion, 2: infrastructure.
#
# 1. GOLDEN. For every fixture, the ORIGINAL script (entrypoint.legacy.sh, entrypoint.sh as it was
#    before the move, kept here only as the reference) and the current one are each run in their own
#    scratch tree on the same inputs: the original's four config reads and its provider branch, and
#    the current script's facts block and provider branch. Both must report the same provider and
#    flags, exit the same way, skip the same MCP servers, and leave byte-identical files behind
#    (~/.codex/config.toml, ~/.gemini/settings.json, the copied or removed credential files).
# 2. FAIL CLOSED. A configuration the original read as "claude" (unreadable JSON, a provider that is
#    not a single value) now stops the script with exit 1 before any credential is touched.
# 3. KEY ISOLATION. The head of the current script, run unmodified with a hosted key in its
#    environment and a `dotnet` shim that records what each call can see: no call sees the key's
#    name or value, the key file gets the value at mode 0400, and nothing carries it afterwards.
#
# Every /root/, /app/, /workspace/ and /run/ literal is rewritten into the scratch tree, and the
# agent binary is given the same tree through `prepare --root`, so nothing outside it is touched.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$REPO_ROOT/tests/entrypoint-prepare"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
trap 'fail_infra "command failed at line $LINENO"' ERR

LEGACY="$HERE/entrypoint.legacy.sh"
CURRENT="$REPO_ROOT/entrypoint.sh"
FIXTURES="$HERE/fixtures"
AGENT_DLL="${AGENT_DLL:-$REPO_ROOT/src/Fleet.Agent/bin/Release/net10.0/Fleet.Agent.dll}"
[ -f "$CURRENT" ] && [ -f "$LEGACY" ] || fail_infra "run from a phleet checkout"
[ -f "$AGENT_DLL" ] || fail_infra "no agent build at $AGENT_DLL (dotnet build src/Fleet.Agent -c Release, or set AGENT_DLL)"
command -v node >/dev/null || fail_infra "node is required (the original script's programs)"
command -v python3 >/dev/null || fail_infra "python3 is required (the original script's programs)"
REAL_DOTNET="$(command -v dotnet)" || fail_infra "dotnet is required"

# The reference is the script as it was on main before the move (commit cb6153f2), byte for byte.
# An edited reference would compare the current script with something that never ran in production.
LEGACY_SHA256=46f0e9362275e3760ad0d4d19bb33f8e19871bd8156793da21d1f3fb94b6bd82
legacy_sha="$( (sha256sum "$LEGACY" 2>/dev/null || shasum -a 256 "$LEGACY") | cut -d' ' -f1)"
[ "$legacy_sha" = "$LEGACY_SHA256" ] || fail_assert "entrypoint.legacy.sh is not the original script (sha256 $legacy_sha)"

WORK=$(mktemp -d)
# KEEP_WORK=1 leaves the scratch trees behind and prints where, to look at what each side wrote.
if [ -n "${KEEP_WORK:-}" ]; then
    trap 'echo "kept: $WORK"' EXIT
else
    trap 'chmod -R u+rwX "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT
fi

DEFAULT_ROOT='check_for_update_on_startup = false

[analytics]
enabled = false

[history]
persistence = "none"
'

# ── cut the blocks under test out of the two scripts ─────────────────────────

# $1 = file, $2 = how the block's first line starts, $3 = how the line that ends it starts (not
# copied). Plain text, not patterns.
cut_block() {
    BLOCK_START="$2" BLOCK_STOP="$3" awk '
        !copy && index($0, ENVIRON["BLOCK_START"]) == 1 {copy=1}
        copy && index($0, ENVIRON["BLOCK_STOP"]) == 1 {exit}
        copy' "$1"
}
CHAIN_START='if [ "$PROVIDER" = "gemini" ]; then'
CHAIN_STOP='# Git identity'

cut_block "$LEGACY" '#!/bin/bash' 'if [ "$HOSTED_PROVIDER" = "true" ]; then' > "$WORK/legacy.facts"
cut_block "$LEGACY" "$CHAIN_START" "$CHAIN_STOP" > "$WORK/legacy.chain"
cut_block "$CURRENT" '# Facts from the generated configuration' '# Hosted model provider (#335 D5)' > "$WORK/current.facts"
cut_block "$CURRENT" "$CHAIN_START" "$CHAIN_STOP" > "$WORK/current.chain"
cut_block "$CURRENT" '#!/bin/bash' '# Secret placeholders. When this agent holds a bound secret' > "$WORK/current.head"
for part in legacy.facts legacy.chain current.facts current.chain current.head; do
    [ -s "$WORK/$part" ] || fail_assert "could not cut $part out of its script"
done
grep -q "Agent.Provider" "$WORK/legacy.facts" || fail_assert "the original's config reads are not in legacy.facts"
grep -q "fleet_prepare facts" "$WORK/current.facts" || fail_assert "the current script does not call 'fleet_prepare facts'"
grep -q "fleet_prepare config" "$WORK/current.chain" || fail_assert "the current provider branch does not call 'fleet_prepare config'"
! grep -qE '(^|[^A-Za-z_])(node|python3) ' "$WORK/current.head" "$WORK/current.chain" \
    || fail_assert "the current script still runs node or python"

# Rewrites every container path in stdin into the tree $1.
into_tree() {
    sed -e "s|/root/|$1/root/|g" -e "s|/app/|$1/app/|g" -e "s|/workspace/|$1/workspace/|g" -e "s|/run/|$1/run/|g"
}

FACTS_OUT='printf "PROVIDER=%s\nHOSTED_PROVIDER=%s\nHOSTED_KEY_ENV=%s\nCLAUDE_LOCAL_MODEL=%s\n" "$PROVIDER" "$HOSTED_PROVIDER" "$HOSTED_KEY_ENV" "$CLAUDE_LOCAL_MODEL"'

# $1 = tree. The original: its config reads, then its provider branch.
write_legacy_script() {
    {
        into_tree "$1" < "$WORK/legacy.facts"
        echo "$FACTS_OUT > '$1/facts'"
        into_tree "$1" < "$WORK/legacy.chain"
        echo 'echo CHAIN-COMPLETED'
    } > "$1/run.sh"
}

# $1 = tree. The current script: its facts block, then its provider branch, with the agent binary
# pointed at the tree. HOME is the container's, so ~/.gemini resolves to <tree>/root/.gemini.
write_current_script() {
    {
        echo '#!/bin/bash'
        echo 'set -u'
        echo "fleet_prepare() { HOME=/root '$REAL_DOTNET' '$AGENT_DLL' prepare \"\$@\" --root '$1'; }"
        into_tree "$1" < "$WORK/current.facts"
        echo "$FACTS_OUT > '$1/facts'"
        into_tree "$1" < "$WORK/current.chain"
        echo 'echo CHAIN-COMPLETED'
    } > "$1/run.sh"
}

# $1 = tree, $2 = appsettings fixture, $3 = mcp fixture or -, $4 = codex-root kind
# (file, missing, directory, raw), $5 = existing gemini settings fixture or -.
seed_tree() {
    mkdir -p "$1/app" "$1/workspace" "$1/root/.gemini" "$1/root/.codex" "$1/root/.codex-host" "$1/run"
    cp "$FIXTURES/appsettings/$2" "$1/app/appsettings.json"
    [ "$3" = - ] || cp "$FIXTURES/mcp/$3" "$1/workspace/.mcp.json"
    case "$4" in
        file) printf '%s' "$DEFAULT_ROOT" > "$1/app/codex-root.toml" ;;
        missing) ;;
        directory) mkdir "$1/app/codex-root.toml" ;;
        # A byte-order mark, CRLF line ends and no final newline: copied through as bytes.
        raw) printf '\357\273\277model = "x"\r\n[history]\r\npersistence = "none"' > "$1/app/codex-root.toml" ;;
        *) fail_infra "unknown codex-root kind $4" ;;
    esac
    [ "$5" = - ] || cp "$FIXTURES/settings/$5" "$1/root/.gemini/settings.json"
    # What the branches' credential handling needs, and something stale for them to deal with.
    printf '{}' > "$1/root/.gemini/oauth_creds.json"
    printf '{"from":"host"}' > "$1/root/.codex-host/auth.json"
    printf '{"from":"an earlier model"}' > "$1/root/.codex/auth.json"
    printf 'stale = true\n' > "$1/root/.codex/config.toml"
}

# $1 = tree; runs its script with the container's HOME. Sets RC.
run_tree() {
    RC=0
    env -i PATH="$PATH" HOME="$1/root" bash "$1/run.sh" > "$1/out" 2> "$1/err" || RC=$?
}

# ── 1. golden ────────────────────────────────────────────────────────────────

GOLDEN=0
# $1 = case name, $2..$5 as seed_tree, $6 = a file under root/ that must exist afterwards, or -,
# $7 = text that file must contain, or -.
golden() {
    local name="$1" old="$WORK/$1/legacy" new="$WORK/$1/current"
    seed_tree "$old" "$2" "$3" "$4" "$5"
    seed_tree "$new" "$2" "$3" "$4" "$5"
    write_legacy_script "$old"
    write_current_script "$new"
    run_tree "$old"; local old_rc=$RC
    run_tree "$new"; local new_rc=$RC

    [ "$old_rc" -eq "$new_rc" ] || { cat "$new/err" >&2; fail_assert "$name: the original exited $old_rc, the current script $new_rc"; }
    [ "$old_rc" -eq 0 ] || fail_assert "$name: the original exited $old_rc; a golden case must run to the end"
    grep -qx CHAIN-COMPLETED "$new/out" || fail_assert "$name: the current provider branch did not complete"
    cmp -s "$old/facts" "$new/facts" \
        || { diff "$old/facts" "$new/facts" >&2 || true; fail_assert "$name: provider or flags differ"; }
    diff -r "$old/root" "$new/root" > "$WORK/diff" 2>&1 \
        || { cat "$WORK/diff" >&2; fail_assert "$name: the files written differ"; }
    diff <(grep '^WARN: skipping MCP server' "$old/err" || true) <(grep '^WARN: skipping MCP server' "$new/err" || true) > "$WORK/diff" \
        || { cat "$WORK/diff" >&2; fail_assert "$name: the skipped MCP servers differ"; }
    if [ "$6" != - ]; then
        [ -f "$new/root/$6" ] || fail_assert "$name: $6 was not written"
        [ "$7" = - ] || grep -qF -- "$7" "$new/root/$6" || fail_assert "$name: $6 does not contain '$7'"
    fi
    GOLDEN=$((GOLDEN + 1))
    echo "PASS golden $name: $(sed -n 's/^PROVIDER=//p' "$new/facts"), identical files"
}

CODEX=.codex/config.toml GEMINI=.gemini/settings.json

# claude writes no CLI configuration; the credential files are the same.
golden claude-cloud          claude-cloud.json         basic.json      file      -                    - -
golden claude-local          claude-local.json         basic.json      file      -                    - -
golden claude-no-provider    claude-default.json       -               missing   -                    - -

golden codex-cloud           codex-cloud.json          basic.json      file      -                    "$CODEX" 'enabled_tools = ["memory_get", "memory_search"]'
golden codex-headers         codex-cloud-headers.json  basic.json      file      -                    "$CODEX" 'http_headers = { "Authorization" = "Bearer example-read-token" }'
golden codex-odd-values      codex-cloud.json          odd.json        file      -                    "$CODEX" '[mcp_servers.10]'
golden codex-odd-headers     codex-cloud-headers.json  odd.json        file      -                    "$CODEX" '"X-List" = "a,,2"'
golden codex-no-mcp          codex-cloud.json          -               file      -                    "$CODEX" 'check_for_update_on_startup = false'
golden codex-no-servers      codex-cloud.json          no-servers.json file      -                    "$CODEX" 'persistence = "none"'
golden codex-root-missing    codex-cloud.json          basic.json      missing   -                    "$CODEX" '[mcp_servers.alpha]'
golden codex-root-directory  codex-cloud.json          basic.json      directory -                    "$CODEX" '[mcp_servers.alpha]'
golden codex-root-raw-bytes  codex-cloud.json          basic.json      raw       -                    "$CODEX" '[mcp_servers.alpha]'
golden codex-nothing         codex-cloud.json          -               missing   -                    "$CODEX" -
golden codex-mcp-unreadable  codex-cloud.json          broken.json     file      -                    "$CODEX" 'stale = true'
golden codex-local-ollama    codex-local-ollama.json   basic.json      file      -                    "$CODEX" 'enabled_tools = ["memory_get"]'
golden codex-local-lmstudio  codex-local-lmstudio.json basic.json      file      -                    "$CODEX" 'enabled_tools = ["memory_search"]'
golden codex-hosted-zai      codex-hosted-zai.json     basic.json      file      -                    "$CODEX" 'enabled_tools = ["memory_get"]'

golden gemini-mcp            gemini.json               basic.json      missing   -                    "$GEMINI" '"httpUrl": "http://127.0.0.1:8091/journal-files/v1/mcp"'
golden gemini-headers        gemini-headers.json       basic.json      missing   theme.json           "$GEMINI" '"Authorization": "Bearer example-read-token"'
golden gemini-odd-values     gemini.json               odd.json        missing   theme.json           "$GEMINI" '"selectedType": "oauth-personal"'
golden gemini-odd-headers    gemini-headers.json       odd.json        missing   -                    "$GEMINI" '"X-Small": 1e-07'
golden gemini-no-mcp         gemini.json               -               missing   -                    "$GEMINI" '"selectedType": "oauth-personal"'
golden gemini-no-mcp-merge   gemini.json               -               missing   theme.json           "$GEMINI" '"theme": "Dracula"'
golden gemini-no-servers     gemini.json               no-servers.json missing   theme.json           "$GEMINI" '"mcpServers": {}'
golden gemini-settings-bad   gemini.json               basic.json      missing   invalid.json         "$GEMINI" '"selectedType": "oauth-personal"'
golden gemini-settings-array gemini.json               basic.json      missing   array.json           "$GEMINI" '[1, 2]'
golden gemini-security-value gemini.json               -               missing   security-scalar.json "$GEMINI" '"security": 5'
golden gemini-mcp-unreadable gemini.json               broken.json     missing   theme.json           "$GEMINI" '"selectedType": "gemini-api-key"'

# The hosted fixture must have gone through the branch that removes the OpenAI credential, and the
# others through the one that copies it: a golden pass on the wrong branch would prove nothing.
[ ! -e "$WORK/codex-hosted-zai/current/root/.codex/auth.json" ] || fail_assert "a hosted codex agent kept auth.json"
grep -qF '"from":"host"' "$WORK/codex-cloud/current/root/.codex/auth.json" || fail_assert "a codex agent did not get the host's auth.json"
grep -qx 'HOSTED_KEY_ENV=ZAI_CODING_PLAN_API_KEY' "$WORK/codex-hosted-zai/current/facts" || fail_assert "the hosted key name was not reported"
grep -qx 'CLAUDE_LOCAL_MODEL=true' "$WORK/claude-local/current/facts" || fail_assert "claude local mode was not reported"
echo "PASS golden: $GOLDEN fixtures"

# ── 2. fail closed ───────────────────────────────────────────────────────────

# $1 = case name, $2 = appsettings text, $3 = text the refusal must contain.
refused() {
    local tree="$WORK/$1"
    seed_tree "$tree" claude-cloud.json basic.json file -
    printf '%s' "$2" > "$tree/app/appsettings.json"
    printf 'claude host credentials' > "$tree/root/.claude-host.json"
    write_current_script "$tree"
    run_tree "$tree"
    [ "$RC" -eq 1 ] || { cat "$tree/err" >&2; fail_assert "$1: expected exit 1, got $RC"; }
    grep -qF -- "$3" "$tree/err" || { cat "$tree/err" >&2; fail_assert "$1: the refusal does not say '$3'"; }
    ! grep -qx CHAIN-COMPLETED "$tree/out" || fail_assert "$1: the provider branch ran"
    [ ! -e "$tree/root/.claude.json" ] || fail_assert "$1: Claude credentials were copied"
    [ ! -e "$tree/facts" ] || fail_assert "$1: facts were accepted"
    echo "PASS refused $1"
}

refused unreadable-json    '{"Agent": {"Provider": "codex"'                                  'cannot read the agent configuration'
refused provider-not-value '{"Agent": {"Provider": {"name": "codex"}}}'                       "'Agent:Provider' must be a single value"
refused unknown-provider   '{"Agent": {"Provider": "foo"}}'                                   "Unknown provider 'foo'; use claude, codex or gemini."
refused hosted-no-key-name '{"Agent": {"Provider": "codex", "HostedProvider": true}}'         'Agent.HostedProviderKeyEnv is missing or invalid'
refused hosted-bad-key-name '{"Agent": {"Provider": "codex", "HostedProvider": true, "HostedProviderKeyEnv": "a b; rm"}}' 'Agent.HostedProviderKeyEnv is missing or invalid'

# ── 3. key isolation ─────────────────────────────────────────────────────────

KEY_NAME=ZAI_CODING_PLAN_API_KEY
KEY_VALUE='example-hosted-key-value 0123'
grep -qx "for _HOSTED_VAR in $KEY_NAME; do" "$CURRENT" || fail_infra "this section assumes $KEY_NAME is a reserved name"

file_mode() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }

# $1 = case name, $2 = appsettings fixture, $3 = the key's value in the environment ("" = unset).
# Runs the unmodified head of entrypoint.sh. Sets TREE and RC.
run_head() {
    TREE="$WORK/$1"
    seed_tree "$TREE" "$2" basic.json file -
    mkdir -p "$TREE/shim" "$TREE/seen"
    # `dotnet Fleet.Agent.dll prepare …` as the script calls it: record what the call can see, then
    # run the real one against the tree.
    {
        echo '#!/bin/bash'
        echo "env > \"\$(mktemp '$TREE/seen/call.XXXXXX')\""
        echo 'shift'
        echo "HOME=/root exec '$REAL_DOTNET' '$AGENT_DLL' \"\$@\" --root '$TREE'"
    } > "$TREE/shim/dotnet"
    chmod 0755 "$TREE/shim/dotnet"
    {
        into_tree "$TREE" < "$WORK/current.head"
        echo "env > '$TREE/after.env'"
        echo "( set -o posix; set ) > '$TREE/after.set'"
        echo 'echo HEAD-COMPLETED'
    } > "$TREE/run.sh"
    local key_env=()
    [ -z "$3" ] || key_env=("$KEY_NAME=$3")
    RC=0
    env -i PATH="$TREE/shim:$PATH" HOME="$TREE/root" OTHER_VARIABLE=kept ${key_env[@]+"${key_env[@]}"} \
        bash "$TREE/run.sh" > "$TREE/out" 2> "$TREE/err" || RC=$?
}

assert_never_seen() {
    ls "$TREE/seen"/call.* >/dev/null 2>&1 || fail_assert "$1: the agent binary was never called"
    ! grep -rqF -e "$KEY_NAME" -e "$KEY_VALUE" "$TREE/seen" || fail_assert "$1: a prepare call could see the hosted key"
    grep -rqx 'OTHER_VARIABLE=kept' "$TREE/seen" || fail_assert "$1: the recorded environments are not the calls' environments"
}

run_head hosted codex-hosted-zai.json "$KEY_VALUE"
[ "$RC" -eq 0 ] || { cat "$TREE/err" >&2; fail_assert "hosted: the head exited $RC"; }
grep -qx HEAD-COMPLETED "$TREE/out" || fail_assert "hosted: the head did not complete"
assert_never_seen hosted
[ "$(cat "$TREE/run/phleet-hosted-key")" = "$KEY_VALUE" ] || fail_assert "hosted: the key file does not hold the key"
[ "$(file_mode "$TREE/run/phleet-hosted-key")" = 400 ] || fail_assert "hosted: the key file is not mode 0400"
! grep -qF -e "$KEY_NAME" -e "$KEY_VALUE" "$TREE/after.env" || fail_assert "hosted: the key is still in the environment"
! grep -qF -e "$KEY_VALUE" "$TREE/after.set" || fail_assert "hosted: a shell variable still holds the key"
! grep -qF -e "$KEY_VALUE" "$TREE/out" "$TREE/err" || fail_assert "hosted: the key was printed"
echo "PASS hosted agent: no prepare call sees the key; it is in the 0400 file and nowhere else"

run_head not-hosted codex-cloud.json "$KEY_VALUE"
[ "$RC" -eq 0 ] || { cat "$TREE/err" >&2; fail_assert "not hosted: the head exited $RC"; }
assert_never_seen not-hosted
[ ! -e "$TREE/run/phleet-hosted-key" ] || fail_assert "not hosted: a key file was written"
grep -qF "NOTE: unsetting $KEY_NAME" "$TREE/err" || fail_assert "not hosted: the reserved name was dropped without a note"
! grep -qF -e "$KEY_NAME" -e "$KEY_VALUE" "$TREE/after.env" || fail_assert "not hosted: the key is still in the environment"
! grep -qF -e "$KEY_VALUE" "$TREE/after.set" || fail_assert "not hosted: a shell variable still holds the key"
echo "PASS agent without a hosted provider: the reserved name is dropped, with a note, before any call"

for blank in "" "   " "<secret>"; do
    run_head "hosted-blank-${#blank}" codex-hosted-zai.json "$blank"
    [ "$RC" -eq 1 ] || { cat "$TREE/err" >&2; fail_assert "hosted with key '$blank': expected exit 1, got $RC"; }
    grep -qF "ERROR: $KEY_NAME is unset, blank or '<secret>'." "$TREE/err" || fail_assert "hosted with key '$blank': no named error"
    [ ! -e "$TREE/run/phleet-hosted-key" ] || fail_assert "hosted with key '$blank': a key file was written"
done
echo "PASS hosted agent with an unset, blank or placeholder key: exit 1, named, no key file"

echo "PASS entrypoint prepare: golden $GOLDEN, fail closed, key isolation"
