#!/bin/bash
# Read provider from generated appsettings (default: claude)
PROVIDER=$(node -e "try{console.log(require('/app/appsettings.json').Agent.Provider)}catch{console.log('claude')}" 2>/dev/null || echo "claude")

# Hosted model provider (#335 D5). The orchestrator computes both values from the shared registry;
# this script holds no prefix list of its own. When set, the key moves from this environment into a
# 0400 file that the agent reads once and deletes, so PID 1 never carries it.
HOSTED_PROVIDER=$(node -e "try{console.log(require('/app/appsettings.json').Agent.HostedProvider===true?'true':'false')}catch{console.log('false')}" 2>/dev/null || echo "false")
HOSTED_KEY_ENV=$(node -e "try{const v=require('/app/appsettings.json').Agent.HostedProviderKeyEnv;console.log(typeof v==='string'?v:'')}catch{console.log('')}" 2>/dev/null || echo "")
HOSTED_KEY_FILE=/run/phleet-hosted-key

# Claude local model mode (#340 D3). Any non-empty Agent.AnthropicBaseUrl counts, whitespace-only
# included: that is a fault the agent's startup gate rejects, so here it fails closed (no credentials
# copied) rather than open. Only a flag is printed, never the URL.
CLAUDE_LOCAL_MODEL=$(node -e "try{const v=require('/app/appsettings.json').Agent.AnthropicBaseUrl;console.log(v!==undefined&&v!==null&&String(v)!==''?'true':'false')}catch{console.log('false')}" 2>/dev/null || echo "false")

if [ "$HOSTED_PROVIDER" = "true" ]; then
    # Never print the value — only the variable name.
    if ! [[ "$HOSTED_KEY_ENV" =~ ^[A-Z_][A-Z0-9_]*$ ]]; then
        echo "ERROR: hosted provider enabled but Agent.HostedProviderKeyEnv is missing or invalid." >&2
        exit 1
    fi
    _HOSTED_KEY_VALUE="${!HOSTED_KEY_ENV:-}"
    _HOSTED_KEY_TRIMMED="$(printf '%s' "$_HOSTED_KEY_VALUE" | tr -d '[:space:]')"
    if [ -z "$_HOSTED_KEY_TRIMMED" ] || [ "$_HOSTED_KEY_TRIMMED" = "<secret>" ]; then
        echo "ERROR: ${HOSTED_KEY_ENV} is unset, blank or '<secret>'. Put the key in .env, attach ${HOSTED_KEY_ENV} as an Env Ref, then reprovision." >&2
        exit 1
    fi
    if ! ( umask 077 && rm -f "$HOSTED_KEY_FILE" && printf '%s' "$_HOSTED_KEY_VALUE" > "$HOSTED_KEY_FILE" && chmod 0400 "$HOSTED_KEY_FILE" ); then
        rm -f "$HOSTED_KEY_FILE" 2>/dev/null
        echo "ERROR: could not write the ${HOSTED_KEY_ENV} key file ${HOSTED_KEY_FILE}." >&2
        exit 1
    fi
    unset _HOSTED_KEY_VALUE _HOSTED_KEY_TRIMMED
fi

# Unconditionally, for every agent and provider: these names are reserved for hosted routing and
# must not reach PID 1 or anything it starts. A non-hosted agent that carries one loses it here.
# The list must equal HostedModelProviders.KeyEnvVars; EntrypointReservedKeysTests reads the line
# directly below the marker, so keep it one line and in this exact form.
# phleet:reserved-key-names
for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do
    if [ -n "${!_HOSTED_VAR:-}" ] && [ "$_HOSTED_VAR" != "$HOSTED_KEY_ENV" ]; then
        echo "NOTE: unsetting ${_HOSTED_VAR}; it is reserved for hosted-provider routing and this agent does not use it." >&2
    fi
    unset "$_HOSTED_VAR"
done
unset _HOSTED_VAR

if [ "$PROVIDER" = "gemini" ]; then
    # Gemini auth — OAuth credentials mounted writable directly as ~/.gemini/oauth_creds.json.
    # Two-level refresh: (1) the CLI's google-auth-library refreshes tokens in-place on expiry;
    # (2) AuthTokenRefreshWorkflow provides a host-side safety net every 30 min, propagating
    # refreshed tokens back to the host to prevent stale-token failures on container restart.
    if [ ! -f "${HOME}/.gemini/oauth_creds.json" ]; then
        echo "ERROR: ~/.gemini/oauth_creds.json not mounted." >&2
        echo "Run setup.sh on the host to complete Gemini OAuth setup, then reprovision the agent." >&2
        exit 1
    fi
    chmod 0600 "${HOME}/.gemini/oauth_creds.json"

    # Enforce OAuth-only auth (issue #132 MUST NOT #2). The gemini CLI auto-detects
    # GEMINI_API_KEY / GOOGLE_API_KEY and prefers them over oauth_creds.json. A leftover
    # API key in the cluster .env (e.g. from the PR #129 SDK era) would silently route
    # traffic through the API-key billing tier instead of OAuth, surfacing as confusing
    # 429 "prepayment credits depleted" errors mid-task. Fail fast with a clear message
    # so the operator can clean up .env before the agent starts taking traffic.
    if [ -n "${GEMINI_API_KEY:-}" ] || [ -n "${GOOGLE_API_KEY:-}" ]; then
        echo "ERROR: GEMINI_API_KEY or GOOGLE_API_KEY is set but provider=gemini uses OAuth." >&2
        echo "Remove the API key from ./fleet/.env (and the agent's env refs), then reprovision." >&2
        echo "OAuth credentials at ~/.gemini/oauth_creds.json are the single source of truth." >&2
        exit 1
    fi

    # Trust the workspace directory so --yolo is not silently downgraded to "default"
    # approval mode. Without this the CLI prompts for every tool call approval, which
    # hangs indefinitely in headless mode (no human present to approve).
    export GEMINI_CLI_TRUST_WORKSPACE=true

    # Translate /workspace/.mcp.json → ~/.gemini/settings.json (mcpServers block).
    # The gemini CLI reads MCP server config exclusively from ~/.gemini/settings.json.
    # It does NOT auto-discover fleet's /workspace/.mcp.json. Without this step gemini
    # agents cannot call any MCP tools (memory, temporal, orchestrator, playwright, etc.).
    # stdio-transport servers are skipped; only HTTP/SSE is supported by the gemini CLI.
    MCP_CONFIG="/workspace/.mcp.json"
    GEMINI_SETTINGS="${HOME}/.gemini/settings.json"
    mkdir -p "${HOME}/.gemini"
    if [ -f "${MCP_CONFIG}" ]; then
        python3 - "${MCP_CONFIG}" "${GEMINI_SETTINGS}" <<'PYEOF'
import json, sys, os

mcp_path, settings_path = sys.argv[1], sys.argv[2]
with open(mcp_path) as f:
    mcp = json.load(f)

servers = mcp.get("mcpServers", {})
gemini_servers = {}

for name, cfg in servers.items():
    url = cfg.get("url", "")
    # Gemini CLI mcpServers schema accepts only "url" — the transport is inferred
    # from the URL scheme. Do NOT include "transport" or "type" keys; the CLI
    # rejects unknown fields with "Unrecognized key(s) in object" and drops the server.
    if url:
        gemini_servers[name] = {"url": url}
    else:
        print(f"WARN: skipping MCP server '{name}' (no URL — stdio transport not supported by gemini CLI)", file=sys.stderr)

# Merge into existing settings.json (preserves other settings such as theme).
existing = {}
if os.path.exists(settings_path):
    try:
        with open(settings_path) as f:
            existing = json.load(f)
    except Exception:
        pass

existing["mcpServers"] = gemini_servers

# Force OAuth-personal auth selection. Without this key the CLI prints
# "Please set an Auth method in your /root/.gemini/settings.json or specify
#  one of the following environment variables: GEMINI_API_KEY, ..."
# even when oauth_creds.json is present — it has the credentials but no
# instruction to use them. Schema verified against host-side settings.json
# created by `gemini auth login` interactively (v0.40.1).
existing.setdefault("security", {}).setdefault("auth", {})["selectedType"] = "oauth-personal"

with open(settings_path, "w") as f:
    json.dump(existing, f, indent=2)
print(f"Gemini MCP: wrote {len(gemini_servers)} server(s) to {settings_path}")
PYEOF
        if [ $? -ne 0 ]; then
            echo "WARN: Failed to write ${GEMINI_SETTINGS} — gemini agent will start without MCP tools" >&2
        fi
    else
        echo "WARN: ${MCP_CONFIG} not found; gemini agent will start without MCP tools" >&2
        # Even without MCP we must seed the OAuth auth selector — otherwise the CLI
        # falls through to "no Auth method configured" and refuses to run.
        python3 - "${GEMINI_SETTINGS}" <<'PYEOF'
import json, os, sys
settings_path = sys.argv[1]
existing = {}
if os.path.exists(settings_path):
    try:
        with open(settings_path) as f:
            existing = json.load(f)
    except Exception:
        pass
existing.setdefault("security", {}).setdefault("auth", {})["selectedType"] = "oauth-personal"
with open(settings_path, "w") as f:
    json.dump(existing, f, indent=2)
PYEOF
    fi

elif [ "$PROVIDER" = "codex" ]; then
    if [ "$HOSTED_PROVIDER" = "true" ]; then
        # A hosted-provider agent holds no OpenAI credential (#335 D6). /root/.codex persists in the
        # workspace volume, so an auth.json from an earlier model must be removed, not just skipped.
        rm -f /root/.codex/auth.json
    elif [ -f /root/.codex-host/auth.json ]; then
        # Codex auth — always overwrite from host mount (source of truth, kept fresh by AuthTokenRefreshWorkflow)
        mkdir -p /root/.codex
        cp /root/.codex-host/auth.json /root/.codex/auth.json
    fi
    # Codex app-server threads are process-lifetime state only in phleet. Clear any stale
    # persisted sessions on cold start so a reprovision cannot accidentally inherit old rollout state.
    rm -rf /root/.codex/sessions/* 2>/dev/null || true
    # Generate ~/.codex/config.toml with MCP servers + per-server enabled_tools whitelist
    MCP_JSON="/workspace/.mcp.json"
    if [ -f "$MCP_JSON" ]; then
        mkdir -p /root/.codex
        node -e "
const fs = require('fs');
const appsettings = JSON.parse(fs.readFileSync('/app/appsettings.json', 'utf8'));
const allowedTools = (appsettings.Agent && appsettings.Agent.AllowedTools) || [];
const mcpCfg = JSON.parse(fs.readFileSync('$MCP_JSON', 'utf8'));
const servers = mcpCfg.mcpServers || {};
let toml = '';
for (const [name, s] of Object.entries(servers)) {
    if (!s.url) continue;
    const prefix = 'mcp__' + name + '__';
    const enabled = allowedTools
        .filter(t => t.startsWith(prefix))
        .map(t => t.slice(prefix.length));
    toml += '[mcp_servers.' + name + ']\n';
    toml += 'url = \"' + s.url + '\"\n';
    if (enabled.length > 0) {
        toml += 'enabled_tools = [' + enabled.map(t => '\"' + t + '\"').join(', ') + ']\n';
    }
    toml += '\n';
}
if (toml) fs.writeFileSync('/root/.codex/config.toml', toml);
" 2>/dev/null || true
    fi
elif [ "$CLAUDE_LOCAL_MODEL" = "true" ]; then
    # A local-model agent holds no Claude OAuth credential (#340 D3). /root/.claude persists in the
    # workspace volume, so a credentials file from an earlier life must be removed, not just skipped.
    rm -f /root/.claude/.credentials.json
    if [ -e /root/.claude/.credentials.json ] || [ -L /root/.claude/.credentials.json ]; then
        echo "ERROR: Claude local model mode, but /root/.claude/.credentials.json could not be removed (bind-mounted?). Remove the mount, then reprovision." >&2
        exit 1
    fi
else
    # Claude auth — always overwrite from host mount (source of truth, kept fresh by AuthTokenRefreshWorkflow)
    if [ -f /root/.claude-host.json ]; then
        cp /root/.claude-host.json /root/.claude.json
    fi
    if [ -f /root/.claude-host/.credentials.json ]; then
        mkdir -p /root/.claude
        cp /root/.claude-host/.credentials.json /root/.claude/.credentials.json
    fi
fi
# Git identity
git config --global user.name "${GIT_USER_NAME:-Fleet Agent}"
git config --global user.email "${GIT_USER_EMAIL:-fleet@example.com}"
git config --global safe.directory '*'

# GitHub App auth (opt-in: only runs if GITHUB_APP_ID / GITHUB_APP_ID_OVERRIDE + PEM available)
# Prefer GITHUB_APP_PEM_OVERRIDE over GITHUB_APP_PEM so per-agent override takes effect at startup.
_GITHUB_PEM_B64="${GITHUB_APP_PEM_OVERRIDE:-${GITHUB_APP_PEM:-}}"
if [ -n "${_GITHUB_PEM_B64:-}" ]; then
    echo "$_GITHUB_PEM_B64" | base64 -d > /tmp/github-app-key.pem
    chmod 600 /tmp/github-app-key.pem
fi
if [ -n "${GITHUB_APP_ID_OVERRIDE:-${GITHUB_APP_ID:-}}" ] && [ -f /tmp/github-app-key.pem ]; then
    /app/gh-auth.sh
    # Refresh token every 45min (installation tokens expire after 1h).
    # Pass override vars so cron refreshes honour the same App identity as startup.
    echo "*/45 * * * * GITHUB_APP_ID=${GITHUB_APP_ID:-} GITHUB_APP_ID_OVERRIDE=${GITHUB_APP_ID_OVERRIDE:-} /app/gh-auth.sh >> /var/log/gh-auth.log 2>&1" | crontab -
    cron
fi

# Workspace
mkdir -p /workspace/repos /workspace/.fleet/scheduled /workspace/share

# MinIO file sharing — configure mc alias if credentials are present
if [ -n "${MINIO_ACCESS_KEY:-}" ] && [ -n "${MINIO_SECRET_KEY:-}" ]; then
    mc alias set fleet "http://fleet-minio:9000" "$MINIO_ACCESS_KEY" "$MINIO_SECRET_KEY" --api S3v4 > /dev/null 2>&1 || true
fi

# Wait for infrastructure dependencies before starting the agent.
# Prevents crash-restart cycles on macstudio reboot when RabbitMQ/orchestrator
# aren't ready yet. RabbitMqConnectionHelper retries internally too, but waiting
# here avoids noisy crash loops before dotnet even starts.

wait_for_tcp() {
    local host=$1 port=$2 label=$3 deadline=$4
    local delay=2
    echo "Waiting for $label ($host:$port)..."
    while ! (echo > /dev/tcp/$host/$port) 2>/dev/null; do
        if [ "$(date +%s)" -ge "$deadline" ]; then
            echo "ERROR: Timed out waiting for $label after 2 minutes" >&2
            exit 1
        fi
        sleep "$delay"
        delay=$(( delay < 30 ? delay * 2 : 30 ))
    done
    echo "$label is ready."
}

wait_for_http() {
    local url=$1 label=$2 deadline=$3
    local delay=2
    echo "Waiting for $label ($url)..."
    while ! curl -sf "$url" > /dev/null 2>&1; do
        if [ "$(date +%s)" -ge "$deadline" ]; then
            echo "ERROR: Timed out waiting for $label after 2 minutes" >&2
            exit 1
        fi
        sleep "$delay"
        delay=$(( delay < 30 ? delay * 2 : 30 ))
    done
    echo "$label is ready."
}

DEADLINE=$(( $(date +%s) + 120 ))

wait_for_tcp  "rabbitmq"        5672 "RabbitMQ"    "$DEADLINE"
wait_for_http "http://fleet-orchestrator:3600/health" "Orchestrator" "$DEADLINE"

exec dotnet Fleet.Agent.dll
