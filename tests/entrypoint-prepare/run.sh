#!/usr/bin/env bash
# entrypoint.sh no longer reads appsettings.json itself: `dotnet Fleet.Agent.dll prepare` does, and
# writes the provider CLI configuration the script's inline Node and Python used to write. This
# harness proves three things about that move, and a fourth about what changed since.
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
#    (~/.codex/config.toml, the copied or removed credential files). Claude and Codex only: see 4.
# 2. FAIL CLOSED. A configuration the original read as "claude" (unreadable JSON, a provider that is
#    not a single value) now stops the script with exit 1 before any credential is touched.
# 3. KEY ISOLATION. The head of the current script, run unmodified with a hosted key in its
#    environment and a `dotnet` shim that records what each call can see: no call sees the key's
#    name or value, the key file gets the value at mode 0400, and nothing carries it afterwards.
# 4. GEMINI. Changed on purpose, so there is no original to compare with: the original prepared the
#    Gemini CLI (a Google sign-in file, ~/.gemini/settings.json from inline Python) and refused an
#    API key; a gemini agent now runs the Antigravity CLI with GEMINI_API_KEY. The current branch
#    must exit 1 without the key before anything is written, and with it leave exactly the files in
#    fixtures/expected/ as ~/.gemini/config/mcp_config.json, for its owner only, and no settings file.
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
# (file, missing, directory, raw).
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
    # What the branches' credential handling needs, and something stale for them to deal with.
    printf '{"from":"host"}' > "$1/root/.codex-host/auth.json"
    printf '{"from":"an earlier model"}' > "$1/root/.codex/auth.json"
    printf 'stale = true\n' > "$1/root/.codex/config.toml"
}

# $1 = tree, then NAME=value pairs for its environment; runs its script with the container's HOME.
# Sets RC.
run_tree() {
    local tree="$1"; shift
    RC=0
    env -i PATH="$PATH" HOME="$tree/root" "$@" bash "$tree/run.sh" > "$tree/out" 2> "$tree/err" || RC=$?
}

# ── 1. golden ────────────────────────────────────────────────────────────────

GOLDEN=0
# $1 = case name, $2..$4 as seed_tree, $5 = a file under root/ that must exist afterwards, or -,
# $6 = text that file must contain, or -.
golden() {
    local name="$1" old="$WORK/$1/legacy" new="$WORK/$1/current"
    seed_tree "$old" "$2" "$3" "$4"
    seed_tree "$new" "$2" "$3" "$4"
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
    if [ "$5" != - ]; then
        [ -f "$new/root/$5" ] || fail_assert "$name: $5 was not written"
        [ "$6" = - ] || grep -qF -- "$6" "$new/root/$5" || fail_assert "$name: $5 does not contain '$6'"
    fi
    GOLDEN=$((GOLDEN + 1))
    echo "PASS golden $name: $(sed -n 's/^PROVIDER=//p' "$new/facts"), identical files"
}

CODEX=.codex/config.toml

# claude writes no CLI configuration; the credential files are the same.
golden claude-cloud          claude-cloud.json         basic.json      file      - -
golden claude-local          claude-local.json         basic.json      file      - -
golden claude-no-provider    claude-default.json       -               missing   - -

golden codex-cloud           codex-cloud.json          basic.json      file      "$CODEX" 'enabled_tools = ["memory_get", "memory_search"]'
golden codex-headers         codex-cloud-headers.json  basic.json      file      "$CODEX" 'http_headers = { "Authorization" = "Bearer example-read-token" }'
golden codex-odd-values      codex-cloud.json          odd.json        file      "$CODEX" '[mcp_servers.10]'
golden codex-odd-headers     codex-cloud-headers.json  odd.json        file      "$CODEX" '"X-List" = "a,,2"'
golden codex-no-mcp          codex-cloud.json          -               file      "$CODEX" 'check_for_update_on_startup = false'
golden codex-no-servers      codex-cloud.json          no-servers.json file      "$CODEX" 'persistence = "none"'
golden codex-root-missing    codex-cloud.json          basic.json      missing   "$CODEX" '[mcp_servers.alpha]'
golden codex-root-directory  codex-cloud.json          basic.json      directory "$CODEX" '[mcp_servers.alpha]'
golden codex-root-raw-bytes  codex-cloud.json          basic.json      raw       "$CODEX" '[mcp_servers.alpha]'
golden codex-nothing         codex-cloud.json          -               missing   "$CODEX" -
golden codex-mcp-unreadable  codex-cloud.json          broken.json     file      "$CODEX" 'stale = true'
golden codex-local-ollama    codex-local-ollama.json   basic.json      file      "$CODEX" 'enabled_tools = ["memory_get"]'
golden codex-local-lmstudio  codex-local-lmstudio.json basic.json      file      "$CODEX" 'enabled_tools = ["memory_search"]'
golden codex-hosted-zai      codex-hosted-zai.json     basic.json      file      "$CODEX" 'enabled_tools = ["memory_get"]'


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
    seed_tree "$tree" claude-cloud.json basic.json file
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
    seed_tree "$TREE" "$2" basic.json file
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

# ── 4. gemini: the Antigravity CLI ────────────────────────────────────────────

GEMINI_KEY='example-gemini-key-value 4567'
GEMINI_MCP=.gemini/config/mcp_config.json
GEMINI_CASES=0

# $1 = case name, $2 = appsettings fixture, $3 = mcp fixture or -, $4 = a warning stderr must
# hold, or -. Runs the current provider branch with the key and compares what it wrote with
# fixtures/expected/$1.json.
gemini_writes() {
    local tree="$WORK/$1"
    seed_tree "$tree" "$2" "$3" missing
    # A file from an earlier start, readable by everyone and holding a header value: replaced.
    mkdir -p "$tree/root/.gemini/config"
    printf '{"mcpServers":{"stale":{"url":"http://stale","headers":{"Authorization":"Bearer stale-token"}}}}' > "$tree/root/$GEMINI_MCP"
    chmod 644 "$tree/root/$GEMINI_MCP"
    write_current_script "$tree"
    run_tree "$tree" GEMINI_API_KEY="$GEMINI_KEY"
    [ "$RC" -eq 0 ] || { cat "$tree/err" >&2; fail_assert "$1: the gemini branch exited $RC"; }
    grep -qx CHAIN-COMPLETED "$tree/out" || fail_assert "$1: the gemini branch did not complete"
    grep -qx 'PROVIDER=gemini' "$tree/facts" || fail_assert "$1: the provider was not reported as gemini"
    cmp -s "$FIXTURES/expected/$1.json" "$tree/root/$GEMINI_MCP" \
        || { diff "$FIXTURES/expected/$1.json" "$tree/root/$GEMINI_MCP" >&2 || true; fail_assert "$1: $GEMINI_MCP is not fixtures/expected/$1.json"; }
    [ "$(file_mode "$tree/root/$GEMINI_MCP")" = 600 ] || fail_assert "$1: $GEMINI_MCP is not mode 0600"
    [ "$4" = - ] || grep -qF -- "$4" "$tree/err" || { cat "$tree/err" >&2; fail_assert "$1: no warning '$4'"; }
    [ ! -e "$tree/root/.gemini/settings.json" ] && [ ! -e "$tree/root/.gemini/antigravity-cli/settings.json" ] \
        || fail_assert "$1: a settings file was written"
    [ ! -e "$tree/root/.gemini/oauth_creds.json" ] || fail_assert "$1: a Google sign-in file appeared"
    ! grep -rqF -e "$GEMINI_KEY" "$tree/out" "$tree/err" "$tree/root" || fail_assert "$1: the key was printed or written"
    GEMINI_CASES=$((GEMINI_CASES + 1))
    echo "PASS gemini $1: $GEMINI_MCP as expected, mode 0600"
}

gemini_writes gemini-mcp            gemini.json         basic.json      "WARN: skipping MCP server 'gamma' (it has headers and Agent.McpHeaderSupport is not set)"
gemini_writes gemini-headers        gemini-headers.json basic.json      -
gemini_writes gemini-no-mcp         gemini.json         -               "not found; gemini agent will start without MCP tools"
gemini_writes gemini-no-servers     gemini.json         no-servers.json -
gemini_writes gemini-mcp-unreadable gemini-headers.json broken.json     "gemini agent will start without MCP tools"
# odd.json names two servers twice: a file like that is not used at all, rather than guessed at.
gemini_writes gemini-odd-headers    gemini-headers.json odd.json        "gemini agent will start without MCP tools"
grep -qF '"Authorization": "Bearer example-read-token"' "$WORK/gemini-headers/root/$GEMINI_MCP" || fail_assert "gemini-headers: the header was not carried over"
grep -qF '"command": "node"' "$WORK/gemini-mcp/root/$GEMINI_MCP" || fail_assert "gemini-mcp: the stdio server was left out"
! grep -qF 'example-read-token' "$WORK/gemini-mcp/root/$GEMINI_MCP" || fail_assert "gemini-mcp: a header value was written without header support"

# Without the key the container does not start, nothing is prepared, and the value is never asked for.
for blank in unset "" "   "; do
    tree="$WORK/gemini-no-key-${#blank}"
    seed_tree "$tree" gemini-headers.json basic.json missing
    write_current_script "$tree"
    if [ "$blank" = unset ]; then run_tree "$tree"; else run_tree "$tree" GEMINI_API_KEY="$blank"; fi
    [ "$RC" -eq 1 ] || { cat "$tree/err" >&2; fail_assert "gemini with key '$blank': expected exit 1, got $RC"; }
    grep -qF 'ERROR: GEMINI_API_KEY is not set' "$tree/err" || fail_assert "gemini with key '$blank': no named error"
    ! grep -qx CHAIN-COMPLETED "$tree/out" || fail_assert "gemini with key '$blank': the branch completed"
    [ ! -e "$tree/root/$GEMINI_MCP" ] || fail_assert "gemini with key '$blank': an MCP file was written"
done
echo "PASS gemini without a key (unset, empty, blank): exit 1, named, nothing written"

# The original accepted only a Google sign-in and refused an API key; the current branch must not
# carry either rule over.
! grep -qE 'oauth_creds|GOOGLE_API_KEY|GEMINI_CLI_TRUST_WORKSPACE' "$WORK/current.chain" \
    || fail_assert "the current gemini branch still handles the Gemini CLI's sign-in"

echo "PASS entrypoint prepare: golden $GOLDEN, fail closed, key isolation, gemini $GEMINI_CASES"
