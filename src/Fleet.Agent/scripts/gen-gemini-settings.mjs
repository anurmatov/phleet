#!/usr/bin/env node
// gen-gemini-settings.mjs <mcp-json-path>
// Reads .mcp.json, outputs ~/.gemini/settings.json JSON (stdout).
// Only HTTP/SSE transport servers are included; stdio entries are skipped with a warning.
import fs from 'fs';

const mcpPath = process.argv[2];
if (!mcpPath || !fs.existsSync(mcpPath)) {
  process.stdout.write('{}');
  process.exit(0);
}
let mcpCfg;
try {
  mcpCfg = JSON.parse(fs.readFileSync(mcpPath, 'utf8'));
} catch (e) {
  process.stderr.write(`gen-gemini-settings: failed to parse ${mcpPath}: ${e.message}\n`);
  process.stdout.write('{}');
  process.exit(0);
}
const servers = mcpCfg.mcpServers || {};
const mcpServers = {};
for (const [name, s] of Object.entries(servers)) {
  if (!s.url) {
    process.stderr.write(`gen-gemini-settings: skipping stdio-transport server '${name}'\n`);
    continue;
  }
  mcpServers[name] = { url: s.url };
}
process.stdout.write(JSON.stringify({ mcpServers }, null, 2));
