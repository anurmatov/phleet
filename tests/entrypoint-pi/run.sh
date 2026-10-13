#!/usr/bin/env bash
# #386 A16 (and the entrypoint half of A16b): the `pi` branch of entrypoint.sh.
#
#   bash tests/entrypoint-pi/run.sh                 # branch mode: no Docker, no root
#   bash tests/entrypoint-pi/run.sh --image IMAGE   # image mode: the whole entrypoint, as PID 1
#
# Exit 0: pass, 1: assertion, 2: infrastructure.
#
# BRANCH MODE runs the pi branch exactly as it appears in entrypoint.sh, cut out at its `elif` and
# with every `/root/` rewritten to a scratch root first. The real /root is never touched: the run
# refuses to start if any `/root/` literal survives the rewrite. A file is made undeletable by
# taking write permission off its parent directory, which stops `rm` for any user except root, so
# the branch runs unprivileged: as the caller when that is not root, otherwise as `nobody` through
# `setpriv`. No chattr, capability or mount is needed. Side effect worth knowing: the two
# /root/.pi/agent files share a parent, so making one of them undeletable leaves the other too;
# each case asserts only that its own file is named.
#
# IMAGE MODE runs the real entrypoint.sh as PID 1 in a throwaway container of IMAGE (CI passes the
# agent image it just built). A fake `dotnet` on PATH stands in for the agent and prints the names
# in /proc/1/environ, so the scrub is checked on PID 1 itself. RabbitMQ and the orchestrator are two
# local listeners. A file is made undeletable the way an operator mount would be: bind-mounted,
# read-only, over its path, so `rm` fails with EBUSY even for root. It also checks PV1 (unknown
# provider exits 1) and that an empty or missing provider still starts the claude path.
#
# The expected names come from src/Fleet.Shared/PiLocalModel.cs, never from entrypoint.sh, so this
# also fails when the two lists drift apart.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
trap 'fail_infra "command failed at line $LINENO"' ERR

IMAGE=""
case "${1:-}" in
    "") ;;
    --image) IMAGE="${2:-}"; [ -n "$IMAGE" ] || fail_infra "--image needs an image name" ;;
    *) fail_infra "usage: $0 [--image IMAGE]" ;;
esac

ENTRYPOINT="$REPO_ROOT/entrypoint.sh"
PI_MODEL="$REPO_ROOT/src/Fleet.Shared/PiLocalModel.cs"
if [ ! -f "$ENTRYPOINT" ] || [ ! -f "$PI_MODEL" ]; then fail_infra "run from a phleet checkout"; fi

# The denylist, read from the C# source of truth.
read -r -a NAMES <<< "$(sed -n '/CredentialEnvNames { get; } =/,/\];/p' "$PI_MODEL" | grep -o '"[A-Z0-9_]*"' | tr -d '"' | tr '\n' ' ')"
read -r -a PREFIXES <<< "$(grep -A1 'CredentialEnvPrefixes { get; } =' "$PI_MODEL" | grep -o '"[A-Z0-9_]*"' | tr -d '"' | tr '\n' ' ')"
[ "${#NAMES[@]}" -eq 61 ] || fail_infra "expected 61 names in PiLocalModel.cs, read ${#NAMES[@]}"
[ "${#PREFIXES[@]}" -eq 7 ] || fail_infra "expected 7 prefixes in PiLocalModel.cs, read ${#PREFIXES[@]}"

# One variable per prefix (A8's set), then two that must survive.
PREFIX_SEEDS=(ANTHROPIC_X CLAUDE_CODE_X OPENAI_X CODEX_X GEMINI_X AWS_SESSION_TOKEN GOOGLE_CLOUD_PROJECT)
KEPT=(GH_TOKEN GITHUB_TOKEN)
DENIED=("${NAMES[@]}" "${PREFIX_SEEDS[@]}")
CRED_FILES=(/root/.claude/.credentials.json /root/.codex/auth.json /root/.gemini/oauth_creds.json
    /root/.pi/agent/auth.json /root/.pi/agent/mcp-auth.json)
SESSIONS=/root/.pi/agent/sessions
CRED_TEXT="pi agent must hold no provider credential"

is_denied() {
    local name=$1 p
    for p in "${NAMES[@]}"; do [ "$name" = "$p" ] && return 0; done
    for p in "${PREFIXES[@]}"; do case "$name" in "$p"*) return 0 ;; esac; done
    return 1
}

# Every variable gets a distinct marker value; no output may ever contain one.
SEEDS=()
i=0
for name in "${DENIED[@]}" "${KEPT[@]}"; do
    i=$((i + 1))
    SEEDS+=("$name=PHLEETMARKER${i}VALUE")
done

WORK=$(mktemp -d)
trap 'chmod -R u+rwX "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT
chmod 0755 "$WORK"

# Assertions shared by both modes on a successful pi start: $1 = stdout+stderr, $2 = stderr,
# $3 = the environment names left for the agent, one per line.
assert_scrubbed() {
    local all=$1 err=$2 env_names=$3 name count line
    local note_re='^NOTE: unsetting [A-Z_][A-Z0-9_]*( for a pi agent|; it is reserved for hosted-provider routing and this agent does not use it\.)$'
    if grep -q 'PHLEETMARKER' "$all"; then
        fail_assert "a seeded value reached the output"
    fi
    for name in "${DENIED[@]}"; do
        ! grep -qx "$name" "$env_names" || fail_assert "$name survived the scrub"
        count=$(grep -cE "^NOTE: unsetting ${name}( for a pi agent$|;)" "$err" || true)
        [ "$count" -eq 1 ] || fail_assert "expected one NOTE for $name, found $count"
    done
    while IFS= read -r name; do
        [ -n "$name" ] || continue
        ! is_denied "$name" || fail_assert "$name is on the denylist but reached the agent"
    done < "$env_names"
    for name in "${KEPT[@]}"; do
        grep -qx "$name" "$env_names" || fail_assert "$name must be kept for the agent's git work"
        ! grep -q "NOTE: unsetting ${name}" "$err" || fail_assert "$name was reported as unset"
    done
    while IFS= read -r line; do
        [[ "$line" =~ $note_re ]] || fail_assert "a NOTE line carries more than a name: $line"
    done < <(grep '^NOTE:' "$err" || true)
    echo "PASS scrub: ${#DENIED[@]} denylisted names removed with one name-only NOTE each; ${KEPT[*]} kept"
}

# ── Branch mode ──────────────────────────────────────────────────────────────

run_branch_mode() {
    local branch="$WORK/branch.sh" harness="$WORK/harness.sh" root="$WORK/fakeroot" path literals rewritten
    awk '/^elif \[ "\$PROVIDER" = "pi" \]; then$/ {copy=1; next}
         copy && /^(elif|else|fi)([ ;]|$)/ {exit}
         copy' "$ENTRYPOINT" > "$branch"
    [ -s "$branch" ] || fail_assert "no pi branch found in entrypoint.sh"
    for path in "${CRED_FILES[@]}"; do
        grep -qF "$path" "$branch" || fail_assert "the pi branch does not name $path"
    done
    grep -qF "rm -rf $SESSIONS" "$branch" || fail_assert "the pi branch does not remove $SESSIONS"
    grep -qx '# phleet:pi-credential-env-names' "$branch" || fail_assert "names marker missing from the pi branch"
    grep -qx '# phleet:pi-credential-env-prefixes' "$branch" || fail_assert "prefixes marker missing from the pi branch"

    # Nothing runs until every /root/ literal points into the scratch root.
    literals=$(grep -oF '/root/' "$branch" | wc -l)
    {
        echo '#!/bin/bash'
        sed "s|/root/|$root/|g" "$branch"
        echo 'echo BRANCH-COMPLETED'
        echo 'compgen -e | sed "s/^/ENV /"'
    } > "$harness"
    rewritten=$(grep -oF "$root/" "$harness" | wc -l)
    if [ "$literals" -eq 0 ] || [ "$literals" -ne "$rewritten" ]; then fail_assert "/root rewrite count mismatch"; fi
    ! grep -qF '/root/' "$harness" || fail_assert "a real /root path survived the rewrite"
    chmod 0755 "$harness"

    local -a as_user=()
    if [ "$(id -u)" -eq 0 ]; then
        command -v setpriv >/dev/null || fail_infra "running as root needs setpriv to drop to nobody"
        as_user=(setpriv --reuid=65534 --regid=65534 --clear-groups)
    fi

    seed_root() {
        chmod -R u+rwX "$root" 2>/dev/null || true
        rm -rf "$root"
        local f
        for f in "${CRED_FILES[@]}"; do
            mkdir -p "$(dirname "$root${f#/root}")"
            printf '{"fixture":true}\n' > "$root${f#/root}"
        done
        mkdir -p "$root${SESSIONS#/root}"
        printf '{"fixture":true}\n' > "$root${SESSIONS#/root}/old.jsonl"
        if [ "$(id -u)" -eq 0 ]; then chown -R 65534:65534 "$WORK"; chown 0:0 "$WORK"; fi
    }

    run_branch() {
        local rc=0
        "${as_user[@]}" env -i PATH="$PATH" HOME="$WORK" LANG=C "${SEEDS[@]}" bash "$harness" \
            > "$WORK/out" 2> "$WORK/err" || rc=$?
        cat "$WORK/out" "$WORK/err" > "$WORK/all"
        return "$rc"
    }

    # Every file and the sessions directory are removed, and PID 1's environment is scrubbed.
    seed_root
    local rc=0 f target
    run_branch || rc=$?
    [ "$rc" -eq 0 ] || { cat "$WORK/err" >&2; fail_assert "the pi branch exited $rc on a removable root"; }
    grep -qx BRANCH-COMPLETED "$WORK/out" || fail_assert "the pi branch did not complete"
    for f in "${CRED_FILES[@]}"; do
        [ ! -e "$root${f#/root}" ] || fail_assert "$f was not removed"
    done
    [ ! -e "$root${SESSIONS#/root}" ] || fail_assert "$SESSIONS was not removed"
    echo "PASS removal: all ${#CRED_FILES[@]} credential files and $SESSIONS removed"
    sed -n 's/^ENV //p' "$WORK/out" > "$WORK/env-names"
    assert_scrubbed "$WORK/all" "$WORK/err" "$WORK/env-names"

    # One undeletable file at a time: exit 1, the named text, and the agent never starts.
    for f in "${CRED_FILES[@]}"; do
        seed_root
        target="$root${f#/root}"
        chmod 0555 "$(dirname "$target")"
        rc=0
        run_branch || rc=$?
        chmod 0755 "$(dirname "$target")"
        [ "$rc" -eq 1 ] || fail_assert "expected exit 1 with $f undeletable, got $rc"
        grep -qF "ERROR: $CRED_TEXT, but $target could not be removed" "$WORK/err" \
            || fail_assert "no '$CRED_TEXT' error naming $f"
        ! grep -q BRANCH-COMPLETED "$WORK/out" || fail_assert "the branch continued past an undeletable $f"
        ! grep -q PHLEETMARKER "$WORK/all" || fail_assert "a seeded value reached the output"
        echo "PASS undeletable $f: exit 1 with '$CRED_TEXT'"
    done
}

# ── Image mode ───────────────────────────────────────────────────────────────

run_image_mode() {
    command -v docker >/dev/null || fail_infra "image mode needs docker"
    docker image inspect "$IMAGE" >/dev/null 2>&1 || docker pull -q "$IMAGE" >/dev/null \
        || fail_infra "image $IMAGE is not available"
    mkdir -p "$WORK/harness/bin" "$WORK/creds"

    cat > "$WORK/harness/pid1.sh" <<'PID1'
#!/bin/bash
# PID 1 until it execs the real entrypoint: seed the credential files (a bind-mounted one already
# exists and is left alone), start stand-ins for RabbitMQ and the orchestrator, and put the fake
# dotnet first on PATH.
set -eu
for f in /root/.claude/.credentials.json /root/.codex/auth.json /root/.gemini/oauth_creds.json \
         /root/.pi/agent/auth.json /root/.pi/agent/mcp-auth.json; do
    mkdir -p "$(dirname "$f")"
    [ -e "$f" ] || printf '{"fixture":true}\n' > "$f"
done
mkdir -p /root/.pi/agent/sessions /tmp/orchestrator
printf '{"fixture":true}\n' > /root/.pi/agent/sessions/old.jsonl
: > /tmp/orchestrator/health
(cd /tmp/orchestrator && exec python3 -m http.server 3600 --bind 127.0.0.1 >/dev/null 2>&1) &
(cd /tmp && exec python3 -m http.server 5672 --bind 127.0.0.1 >/dev/null 2>&1) &
export PATH="/harness/bin:$PATH"
exec /bin/bash /app/entrypoint.sh
PID1

    cat > "$WORK/harness/bin/dotnet" <<'DOTNET'
#!/bin/bash
# Stands in for `exec dotnet Fleet.Agent.dll`: report what the agent would inherit, then exit.
echo "FAKE-DOTNET pid=$$ args=$*"
tr '\0' '\n' < /proc/1/environ | cut -d= -f1 | sed 's/^/PID1-ENV /'
for f in /root/.claude/.credentials.json /root/.codex/auth.json /root/.gemini/oauth_creds.json \
         /root/.pi/agent/auth.json /root/.pi/agent/mcp-auth.json /root/.pi/agent/sessions; do
    if [ -e "$f" ]; then echo "PRESENT $f"; else echo "ABSENT $f"; fi
done
DOTNET
    chmod 0755 "$WORK/harness/pid1.sh" "$WORK/harness/bin/dotnet"

    # $1 = appsettings JSON; the rest are extra `docker run` arguments.
    run_container() {
        local settings=$1 rc=0
        shift
        printf '%s\n' "$settings" > "$WORK/appsettings.json"
        timeout 180 docker run --rm --entrypoint /bin/bash \
            --add-host rabbitmq:127.0.0.1 --add-host fleet-orchestrator:127.0.0.1 \
            -v "$ENTRYPOINT:/app/entrypoint.sh:ro" \
            -v "$WORK/appsettings.json:/app/appsettings.json:ro" \
            -v "$WORK/harness:/harness:ro" \
            "$@" "$IMAGE" /harness/pid1.sh > "$WORK/out" 2> "$WORK/err" || rc=$?
        cat "$WORK/out" "$WORK/err" > "$WORK/all"
        [ "$rc" -ne 124 ] || fail_infra "the container did not finish within 180 s"
        return "$rc"
    }

    local pi='{"Agent":{"Provider":"pi"}}' rc f i env_args=()
    for i in "${SEEDS[@]}"; do env_args+=(-e "$i"); done

    rc=0
    run_container "$pi" "${env_args[@]}" || rc=$?
    [ "$rc" -eq 0 ] || { cat "$WORK/all" >&2; fail_assert "the pi entrypoint exited $rc"; }
    grep -q '^FAKE-DOTNET pid=1 ' "$WORK/out" || fail_assert "the entrypoint did not exec the agent as PID 1"
    for f in "${CRED_FILES[@]}" "$SESSIONS"; do
        grep -qx "ABSENT $f" "$WORK/out" || fail_assert "$f is still present when the agent starts"
    done
    echo "PASS image removal: all ${#CRED_FILES[@]} credential files and $SESSIONS removed before exec"
    sed -n 's/^PID1-ENV //p' "$WORK/out" > "$WORK/env-names"
    assert_scrubbed "$WORK/all" "$WORK/err" "$WORK/env-names"

    i=0
    for f in "${CRED_FILES[@]}"; do
        i=$((i + 1))
        printf '{"fixture":true}\n' > "$WORK/creds/$i.json"
        rc=0
        run_container "$pi" "${env_args[@]}" -v "$WORK/creds/$i.json:$f:ro" || rc=$?
        [ "$rc" -eq 1 ] || fail_assert "expected exit 1 with $f bind-mounted, got $rc"
        grep -qF "ERROR: $CRED_TEXT, but $f could not be removed" "$WORK/err" \
            || fail_assert "no '$CRED_TEXT' error naming $f"
        ! grep -q '^FAKE-DOTNET' "$WORK/out" || fail_assert "the agent started beside a mounted $f"
        ! grep -q PHLEETMARKER "$WORK/all" || fail_assert "a seeded value reached the output"
        echo "PASS image undeletable $f: exit 1 with '$CRED_TEXT'"
    done

    rc=0
    run_container '{"Agent":{"Provider":"foo"}}' || rc=$?
    [ "$rc" -eq 1 ] || fail_assert "PV1: provider foo exited $rc, expected 1"
    grep -qF "Unknown provider 'foo'; use claude, codex, gemini or pi." "$WORK/err" || fail_assert "PV1 text missing"
    ! grep -q '^FAKE-DOTNET' "$WORK/out" || fail_assert "PV1: the agent started for provider foo"
    echo "PASS PV1: provider foo exits 1 before the agent starts"

    for settings in '{"Agent":{"Provider":""}}' '{"Agent":{}}'; do
        rc=0
        run_container "$settings" || rc=$?
        if [ "$rc" -ne 0 ] || ! grep -q '^FAKE-DOTNET pid=1 ' "$WORK/out"; then
            cat "$WORK/all" >&2
            fail_assert "an empty or missing provider must still start (claude): $settings"
        fi
        echo "PASS empty provider stays claude: $settings"
    done
}

if [ -n "$IMAGE" ]; then
    run_image_mode
else
    run_branch_mode
fi
echo "PASS entrypoint pi branch (${IMAGE:-branch mode})"
