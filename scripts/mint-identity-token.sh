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
# cannot cover: an MCP client outside the fleet (a laptop pointed at a fleet server) once
# FLEET_AGENT_IDENTITY_ENFORCE=true, and checking by hand what a server should accept.
set -euo pipefail

ENV_FILE="${FLEET_ENV_FILE:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/fleet/.env}"

usage() { echo "usage: $0 <agent|service> <subject>   (key read from ${ENV_FILE}; override with FLEET_ENV_FILE)" >&2; exit 2; }

[[ $# -eq 2 ]] || usage
purpose="$1"; subject="$2"
[[ "$purpose" == "agent" || "$purpose" == "service" ]] || usage
[[ "$subject" =~ ^[A-Za-z0-9_-]{1,128}$ ]] || { echo "subject must be 1-128 of A-Z a-z 0-9 _ -" >&2; exit 2; }
[[ -f "$ENV_FILE" ]] || { echo "no .env at ${ENV_FILE}" >&2; exit 1; }

# The first key signs; any others only verify.
key="$(grep '^FLEET_AGENT_IDENTITY_KEY=' "$ENV_FILE" | head -1 | cut -d= -f2- | cut -d, -f1 | tr -d '[:space:]"'"'"'')"
[[ -n "$key" ]] || { echo "FLEET_AGENT_IDENTITY_KEY is not set in ${ENV_FILE}; run ./upgrade.sh" >&2; exit 1; }

# base64url -> hex, for openssl's key argument.
padded="$(printf '%s' "$key" | tr '_-' '/+')"
while (( ${#padded} % 4 )); do padded+="="; done
hexkey="$(printf '%s' "$padded" | openssl base64 -d -A | od -An -v -tx1 | tr -d ' \n')"
[[ ${#hexkey} -ge 64 ]] || { echo "FLEET_AGENT_IDENTITY_KEY decodes to fewer than 32 bytes" >&2; exit 1; }

mac="$(printf 'fa1|%s|%s' "$purpose" "$subject" \
  | openssl dgst -sha256 -mac HMAC -macopt "hexkey:${hexkey}" -binary \
  | openssl base64 -A | tr '+/' '-_' | tr -d '=')"

printf 'fa1.%s.%s.%s\n' "$purpose" "$subject" "$mac"
