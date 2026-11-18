#!/usr/bin/env bash
# Mints an agent identity token (docs/agent-identity.md) with the key in .env.
#
#   scripts/mint-identity-token.sh service operator     a service token for an operator's own MCP client
#   scripts/mint-identity-token.sh agent <agent-name>   the token an agent is provisioned with
#
# The token is printed to stdout and nothing else is. It is a credential: whoever holds a service
# token is not held to any agent's grants, and whoever holds an agent's token is that agent.
#
# The orchestrator mints agents' tokens itself at provisioning. This script exists for the cases it
# cannot cover: an MCP client outside the fleet (a laptop pointed at a fleet server) with mandatory signed identity, and checking by hand what a server should accept.
set -euo pipefail

ENV_FILE="${FLEET_ENV_FILE:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/fleet/.env}"

usage() { echo "usage: $0 <agent|service> <subject>   (key read from ${ENV_FILE}; override with FLEET_ENV_FILE)" >&2; exit 2; }

check_only=false
if [[ $# -eq 1 && "$1" == --check-key ]]; then
  check_only=true
else
  [[ $# -eq 2 ]] || usage
  purpose="$1"; subject="$2"
  [[ "$purpose" == "agent" || "$purpose" == "service" ]] || usage
  [[ "$subject" =~ ^[A-Za-z0-9_-]{1,128}$ ]] || { echo "invalid identity subject" >&2; exit 2; }
fi
[[ -f "$ENV_FILE" ]] || { echo "identity key file unavailable" >&2; exit 1; }
[[ "$(grep -c '^FLEET_AGENT_IDENTITY_KEY=' "$ENV_FILE")" == 1 ]] || { echo "missing or ambiguous identity key" >&2; exit 1; }
raw="$(grep '^FLEET_AGENT_IDENTITY_KEY=' "$ENV_FILE" | head -1 | cut -d= -f2- || true)"
raw="$(printf '%s' "$raw" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
if [[ "$raw" == \"*\" && ${#raw} -ge 2 ]]; then raw="${raw:1:${#raw}-2}"
elif [[ "$raw" == \'*\' && ${#raw} -ge 2 ]]; then raw="${raw:1:${#raw}-2}"
fi
hexkey=""
IFS=',' read -ra entries <<< "$raw"
for entry in "${entries[@]}"; do
  key="$(printf '%s' "$entry" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  [[ -n "$key" ]] || continue
  [[ "$key" =~ ^[A-Za-z0-9_-]+$ && $((${#key} % 4)) != 1 ]] || { echo "invalid identity key" >&2; exit 1; }
  padded="$(printf '%s' "$key" | tr '_-' '/+')"
  while (( ${#padded} % 4 )); do padded+="="; done
  decoded="$(printf '%s' "$padded" | openssl base64 -d -A | od -An -v -tx1 | tr -d ' \n')"
  [[ ${#decoded} -ge 64 ]] || { echo "invalid identity key" >&2; exit 1; }
  [[ -n "$hexkey" ]] || hexkey="$decoded"
done
[[ -n "$hexkey" ]] || { echo "missing identity key" >&2; exit 1; }
[[ "$check_only" == false ]] || exit 0

mac="$(printf 'fa1|%s|%s' "$purpose" "$subject" \
  | openssl dgst -sha256 -mac HMAC -macopt "hexkey:${hexkey}" -binary \
  | openssl base64 -A | tr '+/' '-_' | tr -d '=')"

printf 'fa1.%s.%s.%s\n' "$purpose" "$subject" "$mac"
