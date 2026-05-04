#!/bin/bash
# Read provider from generated appsettings (default: claude)
PROVIDER=$(node -e "try{console.log(require('/app/appsettings.json').Agent.Provider)}catch{console.log('claude')}" 2>/dev/null || echo "claude")

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
with open(settings_path, "w") as f:
    json.dump(existing, f, indent=2)
print(f"Gemini MCP: wrote {len(gemini_servers)} server(s) to {settings_path}")
PYEOF
        if [ $? -ne 0 ]; then
            echo "WARN: Failed to write ${GEMINI_SETTINGS} — gemini agent will start without MCP tools" >&2
        fi
    else
        echo "WARN: ${MCP_CONFIG} not found; gemini agent will start without MCP tools" >&2
    fi

elif [ "$PROVIDER" = "codex" ]; then
    # Codex auth — always overwrite from host mount (source of truth, kept fresh by AuthTokenRefreshWorkflow)
    if [ -f /root/.codex-host/auth.json ]; then
        mkdir -p /root/.codex
        cp /root/.codex-host/auth.json /root/.codex/auth.json
    fi
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

# GitHub App auth (opt-in: only runs if GITHUB_APP_ID + PEM available)
if [ -n "${GITHUB_APP_PEM:-}" ]; then
    echo "$GITHUB_APP_PEM" | base64 -d > /tmp/github-app-key.pem
    chmod 600 /tmp/github-app-key.pem
fi
if [ -n "${GITHUB_APP_ID:-}" ] && [ -f /tmp/github-app-key.pem ]; then
    /app/gh-auth.sh
    # Refresh token every 45min (installation tokens expire after 1h)
    echo "*/45 * * * * GITHUB_APP_ID=${GITHUB_APP_ID} /app/gh-auth.sh >> /var/log/gh-auth.log 2>&1" | crontab -
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
