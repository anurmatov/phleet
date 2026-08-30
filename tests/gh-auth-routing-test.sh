#!/bin/bash
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
TEST_ROOT=$(mktemp -d)
trap 'rm -rf "$TEST_ROOT"' EXIT

export HOME="$TEST_ROOT/home"
export GH_AUTH_STATE_DIR="$TEST_ROOT/state"
export GH_STUB_LOG="$TEST_ROOT/gh-stub.log"
export GH_STUB_GENERATION="$TEST_ROOT/generation"
mkdir -p "$HOME" "$GH_AUTH_STATE_DIR" "$TEST_ROOT/bin"

# gh-auth.sh signs a real JWT, but all network and gh CLI behavior is local.
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 \
    -out "$GH_AUTH_STATE_DIR/github-app-key.pem" >/dev/null 2>&1

cat > "$TEST_ROOT/bin/curl" <<'CURL_STUB'
#!/bin/bash
set -euo pipefail

URL=""
for arg in "$@"; do
    case "$arg" in
        https://*) URL="$arg" ;;
    esac
done

case "$URL" in
    https://api.github.com/app/installations)
        GENERATION=$(( $(cat "$GH_STUB_GENERATION" 2>/dev/null || echo 0) + 1 ))
        echo "$GENERATION" > "$GH_STUB_GENERATION"
        cat <<'JSON'
[{"id":101,"account":{"login":"owner-a","type":"Organization"}},
 {"id":202,"account":{"login":"owner-b","type":"User"}}]
JSON
        ;;
    https://api.github.com/app/installations/101/access_tokens)
        printf '{"token":"test-token-owner-a-%s","expires_at":"2099-01-01T00:00:00Z"}\n' \
            "$(cat "$GH_STUB_GENERATION")"
        ;;
    https://api.github.com/app/installations/202/access_tokens)
        printf '{"token":"test-token-owner-b-%s","expires_at":"2099-01-01T00:00:00Z"}\n' \
            "$(cat "$GH_STUB_GENERATION")"
        ;;
    *)
        echo "unexpected curl URL" >&2
        exit 1
        ;;
esac
CURL_STUB

cat > "$TEST_ROOT/bin/gh" <<'GH_STUB'
#!/bin/bash
set -euo pipefail

if [ "${1:-}" = "auth" ] && [ "${2:-}" = "login" ]; then
    cat >/dev/null
    # Reproduce the configuration written by gh auth login: the empty scoped
    # helper resets the general helper list, then gh's helper becomes authoritative.
    git config --global --add credential.https://github.com.helper ''
    git config --global --add credential.https://github.com.helper \
        '!/usr/bin/gh-real auth git-credential'
    echo "auth-login" >> "$GH_STUB_LOG"
    exit 0
fi

case "${GH_TOKEN:-}" in
    test-token-owner-a-*) echo "route-owner-a" >> "$GH_STUB_LOG" ;;
    test-token-owner-b-*) echo "route-owner-b" >> "$GH_STUB_LOG" ;;
    *) exit 1 ;;
esac
GH_STUB

cat > "$TEST_ROOT/bin/sleep" <<'SLEEP_STUB'
#!/bin/bash
exit 0
SLEEP_STUB

chmod +x "$TEST_ROOT/bin/curl" "$TEST_ROOT/bin/gh" "$TEST_ROOT/bin/sleep"
export PATH="$TEST_ROOT/bin:$PATH"
export GITHUB_APP_ID=12345

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

assert_routing() {
    local generation=$1
    local host_helpers credentials password

    host_helpers=$(git config --global --get-all \
        credential.https://github.com.helper 2>/dev/null || true)
    [ -z "$host_helpers" ] || fail "host-specific Git helpers still override routing"

    [ "$(git config --global credential.helper)" = \
        "!$GH_AUTH_STATE_DIR/.github-credential-helper.sh" ] || \
        fail "per-owner Git helper is not authoritative"

    credentials=$(printf 'protocol=https\nhost=github.com\npath=owner-a/repo.git\n\n' |
        git credential fill)
    password=$(printf '%s\n' "$credentials" | sed -n 's/^password=//p')
    [ "$password" = "test-token-owner-a-$generation" ] || \
        fail "owner A did not receive owner A credentials"

    credentials=$(printf 'protocol=https\nhost=github.com\npath=owner-b/repo.git\n\n' |
        git credential fill)
    password=$(printf '%s\n' "$credentials" | sed -n 's/^password=//p')
    [ "$password" = "test-token-owner-b-$generation" ] || \
        fail "owner B did not receive owner B credentials"

    gh repo view --repo owner-a/repo >/dev/null
    gh repo view --repo owner-b/repo >/dev/null
}

# First call models container startup.
bash "$REPO_ROOT/gh-auth.sh" >"$TEST_ROOT/startup.log" 2>&1
assert_routing 1

# The cron refresh calls the same script. Tokens rotate, gh auth login writes the
# scoped helpers again, and routing must still use the newly issued owner tokens.
bash "$REPO_ROOT/gh-auth.sh" >"$TEST_ROOT/refresh.log" 2>&1
assert_routing 2

[ "$(grep -c '^auth-login$' "$GH_STUB_LOG")" -eq 2 ] || \
    fail "gh auth login was not exercised on startup and refresh"
[ "$(grep -c '^route-owner-a$' "$GH_STUB_LOG")" -eq 2 ] || \
    fail "gh CLI owner A routing changed"
[ "$(grep -c '^route-owner-b$' "$GH_STUB_LOG")" -eq 2 ] || \
    fail "gh CLI owner B routing changed"

if grep -Fq 'test-token-owner-' "$TEST_ROOT/startup.log" "$TEST_ROOT/refresh.log"; then
    fail "a token value was printed in gh-auth output"
fi

echo "gh-auth per-owner routing test passed"
