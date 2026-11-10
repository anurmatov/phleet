#!/usr/bin/env bash
# Runs inside the client container of run.sh. One phase per invocation; exit 1 on a failed check.
set -euo pipefail
PHASE="$1"
SECRET="${2:-}"
# Two distinctive pieces of the fixture App key: a line of the PEM and the start of its base64 form.
KEY_LINE="${3:-}"
KEY_B64="${4:-}"
API=https://api.example.test
pass() { echo "PASS $PHASE: $*"; }
fail() { echo "FAIL $PHASE: $*" >&2; exit 1; }
status() { curl -s -o /tmp/body -D /tmp/head -w '%{http_code}' "$@" || echo "curl-exit-$?"; }
refusal() { grep -i '^x-fleet-secrets-error:' /tmp/head | tr -d '\r' | awk '{print $2}'; }
expect() { [ "$2" = "$3" ] || fail "$1: expected '$3', got '$2'"; pass "$1"; }

case "$PHASE" in
setup)
    # The real lines of entrypoint.sh that trust the CA, then the real placeholder-mode GitHub setup.
    awk '/^# Secret placeholders\. When this agent holds a bound secret/{copy=1} /^if \[ "\$PROVIDER" = "gemini" \]/{copy=0} copy' \
        /app/entrypoint.sh > /tmp/trust.sh
    grep -q update-ca-certificates /tmp/trust.sh || fail "could not extract the CA block from entrypoint.sh"
    bash /tmp/trust.sh
    bash /app/gh-auth.sh --placeholder
    [ -x "$(dirname "$(command -v gh)")/gh-real" ] || fail "gh wrapper not installed"
    [ ! -e /tmp/github-app-key.pem ] || fail "a key file was written in placeholder mode"
    ls /tmp/.github-token-* > /dev/null 2>&1 && fail "token files were written in placeholder mode"
    pass "CA trusted, gh wrapper and git credential helper installed, no key or token on disk"
    ;;

granted)
    expect "curl bearer" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/v1/whoami)" 200
    expect "curl without a credential passes through" "$(status $API/public)" 200
    expect "node fetch" "$(node -e "fetch('$API/v1/whoami',{headers:{authorization:'Bearer '+process.env.EXAMPLE_API_KEY}}).then(r=>console.log(r.status)).catch(e=>console.log('error '+(e.cause&&e.cause.code||e.message)))")" 200
    expect "python urllib" "$(python3 -c "
import os, urllib.request
r = urllib.request.Request('$API/v1/whoami', headers={'Authorization': 'Bearer ' + os.environ['EXAMPLE_API_KEY']})
print(urllib.request.urlopen(r).status)")" 200

    # Wrong destinations and wrong places: refused by the forwarder, never relayed.
    expect "placeholder to a host bound to another secret" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" https://api.github.com/user)" 403
    expect "... refusal code" "$(refusal)" secret_destination_mismatch
    expect "placeholder in the URL" "$(status "$API/v1/whoami?token=$EXAMPLE_API_KEY")" 403
    expect "... refusal code" "$(refusal)" secret_slot_mismatch
    expect "placeholder in another header" "$(status -H "X-Debug: $EXAMPLE_API_KEY" $API/v1/whoami)" 403
    expect "... refusal code" "$(refusal)" secret_slot_mismatch
    expect "redirect is returned, not followed" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/redirect)" 302
    expect "reflected credential is withheld" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/echo)" 502
    expect "... refusal code" "$(refusal)" secret_reflected
    grep -q "$SECRET" /tmp/head /tmp/body && fail "the secret reached the client"
    expect "unbound host is not steered" "$(getent hosts unbound.example.test | wc -l | tr -d ' ')" 0

    # GitHub: REST by URL owner, by primary, by wrapper hint; GraphQL by wrapper hint; git over HTTPS.
    expect "curl GH_TOKEN, URL owner" "$(curl -s -H "Authorization: Bearer $GH_TOKEN" https://api.github.com/repos/solo-dev/notes | jq -r .installation)" 11
    expect "curl GH_TOKEN, primary" "$(curl -s -H "Authorization: Bearer $GH_TOKEN" https://api.github.com/user | jq -r .installation)" 22
    cd /tmp
    expect "gh api, primary" "$(gh api user --jq .installation)" 22
    expect "gh api, endpoint owner" "$(gh api repos/solo-dev/notes --jq .installation)" 11
    rm -rf /tmp/checkout && git init -q /tmp/checkout && git -C /tmp/checkout remote add origin https://github.com/solo-dev/notes.git
    expect "gh graphql inside a checkout" "$(cd /tmp/checkout && gh api graphql -f query='{viewer{login}}' --jq .data.viewer.login)" installation-11
    expect "gh graphql outside a checkout" "$(gh api graphql -f query='{viewer{login}}' --jq .data.viewer.login)" installation-22
    expect "git ls-remote, organisation" "$(git ls-remote https://github.com/acme/widgets.git 2>/dev/null | grep -c refs/heads/main)" 1
    expect "git ls-remote, personal account" "$(git ls-remote https://github.com/solo-dev/notes.git 2>/dev/null | grep -c refs/heads/main)" 1

    # Nothing in this container is a secret: not the environment, not a file.
    [ -n "$SECRET" ] && [ -n "$KEY_LINE" ] && [ -n "$KEY_B64" ] || fail "leak scan has no needles"
    for needle in "$SECRET" ghs_synthetic "$KEY_LINE" "$KEY_B64"; do
        if env | grep -q -- "$needle" || tr '\0' '\n' < /proc/1/environ | grep -q -- "$needle"; then
            fail "a secret, installation token or key is in the container environment"
        fi
        if grep -rIl -- "$needle" /root /tmp /etc /home /workspace 2>/dev/null | grep -v '^/etc/ssl/' | grep -q .; then
            fail "a secret, installation token or key is in a file: $(grep -rIl -- "$needle" /root /tmp /etc /home /workspace 2>/dev/null | head -3)"
        fi
    done
    pass "no secret value, installation token or private key in the environment or on disk"
    ;;

revoked)
    expect "revoked grant is refused" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/v1/whoami)" 403
    expect "... refusal code" "$(refusal)" secret_grant_unknown
    expect "the other grant still works" "$(curl -s -H "Authorization: Bearer $GH_TOKEN" https://api.github.com/user | jq -r .installation)" 22
    ;;

rotated)
    # The state now holds a value the upstream does not accept: the new value is what was sent.
    expect "rotated value is used at once" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/v1/whoami)" 401
    ;;

restored)
    expect "restored grant and value work again" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/v1/whoami)" 200
    ;;

stateless|stale)
    # No usable state: the forwarder has no certificate to offer (a new connection fails its
    # handshake) and answers 503 on a connection that was already open.
    code=$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/v1/whoami)
    case "$code" in
        503|curl-exit-*|000*) pass "state missing or too old: refused ($code)" ;;
        *) fail "state missing or too old: expected a refusal, got $code" ;;
    esac
    code=$(status -H "Authorization: Bearer $GH_TOKEN" https://api.github.com/user)
    case "$code" in
        503|curl-exit-*|000*) pass "... and for the other grant ($code)" ;;
        *) fail "the other grant was still honoured: $code" ;;
    esac
    ;;

foreign)
    # Another container, another address, the same placeholders.
    expect "placeholder replayed from another address" "$(status -H "Authorization: Bearer $EXAMPLE_API_KEY" $API/v1/whoami)" 403
    expect "... refusal code" "$(refusal)" secret_grant_unknown
    expect "GitHub placeholder replayed from another address" "$(status -H "Authorization: Bearer $GH_TOKEN" https://api.github.com/user)" 403
    ;;
*) fail "unknown phase" ;;
esac
