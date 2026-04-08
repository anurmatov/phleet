#!/bin/bash
set -euo pipefail

APP_ID="${GITHUB_APP_ID:-}"

# Ensure PEM is available at /tmp/github-app-key.pem (handles cron re-runs)
if [ ! -f /tmp/github-app-key.pem ]; then
    if [ -n "${GITHUB_APP_PEM:-}" ]; then
        echo "$GITHUB_APP_PEM" | base64 -d > /tmp/github-app-key.pem
        chmod 600 /tmp/github-app-key.pem
    fi
fi
PEM_FILE=/tmp/github-app-key.pem

if [ -z "$APP_ID" ] || [ ! -f "$PEM_FILE" ]; then
    echo "[gh-auth] GitHub App credentials not found, skipping"
    exit 0
fi

# GITHUB_ACCOUNT is deprecated — token routing is now automatic via per-account token files
if [ -n "${GITHUB_ACCOUNT:-}" ]; then
    echo "[gh-auth] WARNING: GITHUB_ACCOUNT env var is deprecated and ignored — routing is automatic"
fi

# Random jitter (0-10s) to stagger concurrent container starts
JITTER=$((RANDOM % 11))
if [ "$JITTER" -gt 0 ]; then
    echo "[gh-auth] Jitter delay: ${JITTER}s"
    sleep "$JITTER"
fi

echo "[gh-auth] Authenticating as GitHub App $APP_ID"

# JWT (RS256): iat=-60s for clock skew, exp=+10min
HEADER=$(echo -n '{"alg":"RS256","typ":"JWT"}' | openssl base64 -e -A | tr '+/' '-_' | tr -d '=')
NOW=$(date +%s)
PAYLOAD=$(echo -n "{\"iat\":$((NOW-60)),\"exp\":$((NOW+600)),\"iss\":\"${APP_ID}\"}" | openssl base64 -e -A | tr '+/' '-_' | tr -d '=')
SIGNATURE=$(echo -n "${HEADER}.${PAYLOAD}" | openssl dgst -sha256 -sign "$PEM_FILE" | openssl base64 -e -A | tr '+/' '-_' | tr -d '=')
JWT="${HEADER}.${PAYLOAD}.${SIGNATURE}"

# Fetch all installations with their account info
INSTALLATIONS=$(curl -sf \
    -H "Authorization: Bearer $JWT" \
    -H "Accept: application/vnd.github+json" \
    https://api.github.com/app/installations | jq -r '.[] | "\(.id) \(.account.login) \(.account.type)"')

if [ -z "$INSTALLATIONS" ]; then
    echo "[gh-auth] ERROR: No installations found"
    exit 1
fi

# Get a token for each installation; store as /tmp/.github-token-{account_login}.
# First org token (or first personal if no org) becomes the primary for gh CLI.
rm -f /tmp/.github-token-*
PRIMARY_TOKEN=""
PRIMARY_EXPIRES=""
PRIMARY_ACCOUNT=""

while IFS=' ' read -r INSTALL_ID ACCOUNT_LOGIN ACCOUNT_TYPE; do
    RESPONSE=$(curl -sf -X POST \
        -H "Authorization: Bearer $JWT" \
        -H "Accept: application/vnd.github+json" \
        "https://api.github.com/app/installations/${INSTALL_ID}/access_tokens")

    TOKEN=$(echo "$RESPONSE" | jq -r '.token')
    EXPIRES=$(echo "$RESPONSE" | jq -r '.expires_at')

    if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
        echo "[gh-auth] WARNING: Failed to get token for installation $INSTALL_ID ($ACCOUNT_LOGIN), skipping"
        continue
    fi

    echo "[gh-auth] Got token for $ACCOUNT_LOGIN ($ACCOUNT_TYPE, installation $INSTALL_ID, expires $EXPIRES)"

    # Store per-account token file
    echo -n "$TOKEN" > "/tmp/.github-token-${ACCOUNT_LOGIN}"

    # Org token always becomes primary (overwrites personal if set first).
    # Personal token only sets primary if no token has been selected yet.
    if [ "$ACCOUNT_TYPE" = "Organization" ]; then
        PRIMARY_TOKEN="$TOKEN"
        PRIMARY_EXPIRES="$EXPIRES"
        PRIMARY_ACCOUNT="$ACCOUNT_LOGIN"
    elif [ -z "$PRIMARY_ACCOUNT" ]; then
        PRIMARY_TOKEN="$TOKEN"
        PRIMARY_EXPIRES="$EXPIRES"
        PRIMARY_ACCOUNT="$ACCOUNT_LOGIN"
    fi
done <<< "$INSTALLATIONS"

if [ -z "$PRIMARY_TOKEN" ]; then
    echo "[gh-auth] ERROR: Failed to get any installation token"
    exit 1
fi

echo -n "$PRIMARY_TOKEN" > /tmp/.github-token-primary
echo "[gh-auth] Primary account: $PRIMARY_ACCOUNT"

# Install a transparent gh wrapper (idempotent — only runs on first startup, not on cron refreshes).
# The wrapper routes GH_TOKEN to the correct per-account token file based on --repo or git remote owner,
# so all gh commands work across multiple accounts without any manual GH_TOKEN overrides.
GH_BIN=$(command -v gh)
GH_REAL="${GH_BIN%/*}/gh-real"
if [ ! -f "$GH_REAL" ]; then
    mv "$GH_BIN" "$GH_REAL"
    cat > "$GH_BIN" << 'GHWRAPPER'
#!/bin/bash
# Transparent gh wrapper — routes GH_TOKEN per repo owner so all GitHub App accounts work transparently.
# Installed by gh-auth.sh on first startup. Real gh binary is at gh-real in the same directory.

OWNER=""
PREV=""
for arg in "$@"; do
    if [ "$PREV" = "--repo" ] || [ "$PREV" = "-R" ]; then
        OWNER="${arg%%/*}"
        break
    fi
    PREV="$arg"
done

if [ -z "$OWNER" ]; then
    REMOTE=$(git remote get-url origin 2>/dev/null || true)
    OWNER=$(echo "$REMOTE" | sed -n 's|.*github\.com[:/]\([^/]*\)/.*|\1|p')
fi

TOKEN_FILE="/tmp/.github-token-${OWNER}"
if [ -z "$OWNER" ] || [ ! -f "$TOKEN_FILE" ]; then
    TOKEN_FILE="/tmp/.github-token-primary"
fi

exec env GH_TOKEN="$(cat "$TOKEN_FILE" 2>/dev/null)" "$(dirname "$0")/gh-real" "$@"
GHWRAPPER
    chmod +x "$GH_BIN"
    echo "[gh-auth] Installed transparent gh wrapper at $GH_BIN"
fi

# Write a credential helper that routes git HTTPS auth by repo owner.
# Git passes protocol=https, host=github.com, path=owner/repo.git on stdin.
# We look up /tmp/.github-token-{owner}; fall back to primary if not found.
# Set up before gh auth login so git works even if gh CLI auth fails.
git config --global credential.useHttpPath true

cat > /tmp/.github-credential-helper.sh << 'HELPER'
#!/bin/bash
# Called by git with the request on stdin: protocol=https, host=github.com, path=owner/repo.git
while IFS='=' read -r key value; do
    [ -z "$key" ] && break
    [[ "$key" == *"["* ]] && continue
    eval "req_${key}=${value}"
done

# Extract owner from path (e.g. "your-org/your-repo.git" -> "your-org")
OWNER=$(echo "${req_path:-}" | cut -d'/' -f1)
TOKEN_FILE="/tmp/.github-token-${OWNER}"
if [ -z "$OWNER" ] || [ ! -f "$TOKEN_FILE" ]; then
    TOKEN_FILE="/tmp/.github-token-primary"
fi

if [ ! -f "$TOKEN_FILE" ]; then
    exit 1
fi

echo "username=x-access-token"
echo "password=$(cat "$TOKEN_FILE")"
HELPER
chmod +x /tmp/.github-credential-helper.sh

git config --global credential.helper '!/tmp/.github-credential-helper.sh'

# Auth gh CLI with the primary token — retry up to 3 times with 5s delay
GH_AUTH_OK=0
for attempt in 1 2 3; do
    if echo "$PRIMARY_TOKEN" | "$GH_REAL" auth login --with-token; then
        GH_AUTH_OK=1
        break
    fi
    echo "[gh-auth] WARNING: gh auth login attempt $attempt failed, retrying in 5s..."
    sleep 5
done
if [ "$GH_AUTH_OK" -eq 0 ]; then
    echo "[gh-auth] ERROR: gh auth login failed after 3 attempts"
    exit 1
fi

echo "[gh-auth] Authenticated, primary token expires $PRIMARY_EXPIRES"
