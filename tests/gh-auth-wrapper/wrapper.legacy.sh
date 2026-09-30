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
