#!/bin/bash
# Read provider from generated appsettings (default: claude)
PROVIDER=$(node -e "try{console.log(require('/app/appsettings.json').Agent.Provider)}catch{console.log('claude')}" 2>/dev/null || echo "claude")

if [ "$PROVIDER" = "codex" ]; then
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

exec dotnet Fleet.Agent.dll
