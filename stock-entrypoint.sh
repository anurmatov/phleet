#!/bin/bash
# Invoked only after the managed enrollment gate; never runs the legacy root/cron setup.
set -euo pipefail
if [ "$(id -u)" = 0 ] || [ -z "${HOME:-}" ] || [ "$HOME" = /root ] || [[ "$HOME" != /* ]]; then
    echo stock_native_nonroot_home_required >&2
    exit 1
fi
# No environment discovery, provider substitution, account login or trust installation.
WORKDIR=$(node <<'JS'
const fs = require('fs');
const path = require('path');
try {
    const app = JSON.parse(fs.readFileSync('/app/appsettings.json', 'utf8'));
    const agent = app.Agent;
    if (!agent || agent.Provider !== 'codex' || agent.HostedProvider === true || agent.HarnessExtra)
        throw new Error();
    const work = agent.WorkDir;
    if (typeof work !== 'string' || !path.isAbsolute(work) || /[\r\n]/.test(work)) throw new Error();
    const mcp = JSON.parse(fs.readFileSync(path.join(work, '.mcp.json'), 'utf8'));
    if (Object.keys(mcp).join() !== 'mcpServers' || Object.keys(mcp.mcpServers).join() !== 'memory') throw new Error();
    const memory = mcp.mcpServers.memory;
    if (Object.keys(memory).join() !== 'url') throw new Error();
    const url = new URL(memory.url);
    if (url.protocol !== 'https:' || url.pathname !== '/mcp' || url.search || url.hash || url.username || url.password
        || (url.port && url.port !== '443')) throw new Error();
    const tools = (agent.AllowedTools || []).filter(t => ['mcp__memory__memory_get', 'mcp__memory__memory_search', 'mcp__memory__memory_list'].includes(t))
        .map(t => t.slice('mcp__memory__'.length));
    if (!tools.length) throw new Error();
    // The managed gate compares this artifact to the shared HarnessDefaults plan.
    // Copy that verified artifact; do not calculate another set of mode defaults here.
    const root = fs.readFileSync('/app/codex-root.toml', 'utf8');
    const auth = fs.readFileSync(path.join(process.env.HOME, '.codex-host', 'auth.json'));
    const directory = path.join(process.env.HOME, '.codex');
    fs.mkdirSync(directory, {recursive: true, mode: 0o700});
    fs.writeFileSync(path.join(directory, 'auth.json'), auth, {mode: 0o600});
    fs.chmodSync(path.join(directory, 'auth.json'), 0o600);
    fs.writeFileSync(path.join(directory, 'config.toml'), root + '\n[mcp_servers.memory]\nurl = ' + JSON.stringify(memory.url)
        + '\nenabled_tools = ' + JSON.stringify(tools) + '\n', {mode: 0o600});
    console.log(work);
} catch (_) {
    console.error('stock_native_prerequisite_invalid');
    process.exit(1);
}
JS
)
# Do not inherit an alternate native config root. The generated home is the only profile here.
unset CODEX_HOME
cd "$WORKDIR"
exec dotnet /app/Fleet.Agent.dll --contentRoot /app
