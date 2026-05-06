#!/usr/bin/env node
/**
 * gemini-bridge.mjs — persistent bridge between .NET GeminiExecutor and @google/genai SDK.
 *
 * Mirrors codex-bridge.mjs: a long-lived Node.js process that holds a Gemini Chat session
 * across tasks, eliminating the per-task system-prompt re-transmission cost incurred by
 * the previous `gemini -p` per-task spawn approach. See issue #145.
 *
 * Protocol:
 *   stdin:  one JSON line per task from .NET
 *     {"type":"task","prompt":"...","systemPrompt":"...","model":"gemini-2.5-flash",
 *      "attachments":[{"path":"/abs/path/file.jpg","mimeType":"image/jpeg"}]}
 *     {"type":"command","prompt":"..."}    <- handled as regular task
 *   stdout: JSONL events streamed back (same shape as codex-bridge for C# parity)
 *     {"type":"ack"}
 *     {"type":"turn.started"}
 *     {"type":"item.started","itemType":"message","text":"..."}      <- text chunk
 *     {"type":"item.started","itemType":"tool_use","toolName":"...","toolArgs":"..."}
 *     {"type":"item.completed","itemType":"tool_result","text":"..."}
 *     {"type":"turn.completed","text":"...","usage":{...},"durationMs":0}
 *     {"type":"turn.failed","error":"..."}
 *     {"type":"error","message":"..."}
 *
 * Auth: OAuth credentials at ~/.gemini/oauth_creds.json (writable bind mount).
 * google-auth-library's OAuth2Client manages token refresh. Refreshed tokens are
 * persisted back to oauth_creds.json via the 'tokens' event so the host-side
 * AuthTokenRefreshWorkflow and container restarts both see the latest credentials.
 *
 * MCP: Reads server config from --mcp-config (same .mcp.json as codex-bridge).
 * Connects to HTTP MCP servers using the Streamable HTTP transport (JSON-RPC over POST).
 * Tool definitions are fetched once on startup and provided to the model as function
 * declarations. Function calls from the model are routed to the appropriate MCP server.
 *
 * System prompt: delivered once via the JSON envelope's systemPrompt field and set as
 * systemInstruction on the Chat. The Chat session is re-created only when the system
 * prompt or model changes — otherwise history accumulates naturally across tasks.
 */

import { GoogleGenAI } from '@google/genai';
import { OAuth2Client } from 'google-auth-library';
import readline from 'readline';
import fs from 'fs';
import path from 'path';

// ── Auth constants ────────────────────────────────────────────────────────────
// Public installed-app credentials per RFC 8252. Overridable via env vars for
// open-source forks (same pattern as AuthTokenRefreshOptions in the C# bridge).
const GEMINI_CLIENT_ID = process.env.GEMINI_OAUTH_CLIENT_ID
    || '681255809395-oo8ft2oprdrnp9e3aqf6av3hmdib135j.apps.googleusercontent.com';
const GEMINI_CLIENT_SECRET = process.env.GEMINI_OAUTH_CLIENT_SECRET
    || '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';
const CREDS_PATH = path.join(process.env.HOME || '/root', '.gemini', 'oauth_creds.json');

// ── Parse CLI args ────────────────────────────────────────────────────────────
let mcpConfigPath = null;
for (let i = 2; i < process.argv.length; i++) {
    if (process.argv[i] === '--mcp-config' && process.argv[i + 1]) {
        mcpConfigPath = process.argv[i + 1];
        break;
    }
}

// ── Shared state ──────────────────────────────────────────────────────────────
let oauth2Client = null;   // google-auth-library OAuth2Client
let ai = null;             // GoogleGenAI instance
let chat = null;           // Current Chat session (holds conversation history)
let currentSystemPrompt = null;
let currentModel = null;

// MCP state
const mcpServers = {};     // serverName => { client: McpHttpClient, functionDeclarations[] }
const toolServerMap = {};  // toolName   => serverName

// ── Helpers ───────────────────────────────────────────────────────────────────

function emit(obj) {
    process.stdout.write(JSON.stringify(obj) + '\n');
}

function log(msg) {
    process.stderr.write(`[gemini-bridge] ${msg}\n`);
}

// ── OAuth setup ───────────────────────────────────────────────────────────────

function initAuth() {
    if (!fs.existsSync(CREDS_PATH)) {
        throw new Error(
            `OAuth credentials not found at ${CREDS_PATH}. ` +
            `Ensure ~/.gemini/oauth_creds.json is mounted (see entrypoint.sh).`
        );
    }

    const creds = JSON.parse(fs.readFileSync(CREDS_PATH, 'utf8'));
    const client = new OAuth2Client(GEMINI_CLIENT_ID, GEMINI_CLIENT_SECRET);
    client.setCredentials({
        access_token:  creds.access_token,
        refresh_token: creds.refresh_token,
        expiry_date:   creds.expiry_date,
        token_type:    creds.token_type || 'Bearer',
        id_token:      creds.id_token,
        // oauth_creds.json may store scopes as an array or space-separated string
        scope: Array.isArray(creds.scopes)
            ? creds.scopes.join(' ')
            : (creds.scope || creds.scopes),
    });

    // Persist refreshed tokens back to oauth_creds.json so the writable bind mount
    // stays in sync. google-auth-library emits 'tokens' whenever it silently refreshes.
    client.on('tokens', (tokens) => {
        try {
            const existing = fs.existsSync(CREDS_PATH)
                ? JSON.parse(fs.readFileSync(CREDS_PATH, 'utf8'))
                : {};
            if (tokens.access_token)  existing.access_token  = tokens.access_token;
            if (tokens.expiry_date)   existing.expiry_date   = tokens.expiry_date;
            if (tokens.id_token)      existing.id_token      = tokens.id_token;
            if (tokens.refresh_token) existing.refresh_token = tokens.refresh_token;
            // Atomic write (copy-then-rename, matching PR #137 pattern)
            const tmpPath = CREDS_PATH + '.tmp';
            fs.writeFileSync(tmpPath, JSON.stringify(existing, null, 2), { mode: 0o600 });
            fs.renameSync(tmpPath, CREDS_PATH);
            log('Persisted refreshed OAuth tokens');
        } catch (err) {
            log(`WARN: failed to persist refreshed tokens: ${err.message}`);
        }
    });

    return client;
}

// ── MCP Streamable HTTP client ────────────────────────────────────────────────
// Minimal implementation of the MCP Streamable HTTP transport.
// Handles both plain JSON responses and SSE-wrapped responses.

class McpHttpClient {
    constructor(name, url) {
        this.name = name;
        this.url = url;
        this.sessionId = null;
        this._id = 0;
    }

    _nextId() { return ++this._id; }

    async _post(body, expectResponse = true) {
        const headers = {
            'Content-Type': 'application/json',
            'Accept': 'application/json, text/event-stream',
        };
        if (this.sessionId) headers['Mcp-Session-Id'] = this.sessionId;

        let response;
        try {
            response = await fetch(this.url, {
                method: 'POST',
                headers,
                body: JSON.stringify(body),
            });
        } catch (err) {
            throw new Error(`MCP fetch error for '${this.name}': ${err.message}`);
        }

        const sid = response.headers.get('Mcp-Session-Id');
        if (sid) this.sessionId = sid;

        if (!expectResponse) return null;

        if (!response.ok) {
            const text = await response.text().catch(() => '');
            throw new Error(`MCP HTTP ${response.status} from '${this.name}': ${text.slice(0, 300)}`);
        }

        const contentType = response.headers.get('Content-Type') || '';
        if (contentType.includes('text/event-stream')) {
            // Parse the first data event from the SSE response
            const text = await response.text();
            for (const line of text.split('\n')) {
                if (!line.startsWith('data: ')) continue;
                const chunk = line.slice(6).trim();
                if (!chunk || chunk === '[DONE]') continue;
                const msg = JSON.parse(chunk);
                if (msg.error) throw new Error(`MCP error from '${this.name}': ${msg.error.message}`);
                return msg;
            }
            throw new Error(`MCP SSE response from '${this.name}' contained no data events`);
        }

        const json = await response.json();
        return json;
    }

    async initialize() {
        const resp = await this._post({
            jsonrpc: '2.0',
            id: this._nextId(),
            method: 'initialize',
            params: {
                protocolVersion: '2024-11-05',
                capabilities: { tools: {} },
                clientInfo: { name: 'gemini-bridge', version: '1.0.0' },
            },
        });
        if (resp?.error) {
            throw new Error(`MCP initialize error from '${this.name}': ${resp.error.message}`);
        }
        // Initialized notification (no response expected)
        await this._post({
            jsonrpc: '2.0',
            method: 'notifications/initialized',
            params: {},
        }, false).catch(() => { /* notification — ignore transport errors */ });
    }

    async listTools() {
        const resp = await this._post({
            jsonrpc: '2.0',
            id: this._nextId(),
            method: 'tools/list',
            params: {},
        });
        if (resp?.error) {
            throw new Error(`MCP tools/list error from '${this.name}': ${resp.error.message}`);
        }
        return resp?.result?.tools ?? [];
    }

    async callTool(name, args) {
        const resp = await this._post({
            jsonrpc: '2.0',
            id: this._nextId(),
            method: 'tools/call',
            params: { name, arguments: args ?? {} },
        });
        if (resp?.error) {
            throw new Error(`MCP tools/call error from '${this.name}' tool '${name}': ${resp.error.message}`);
        }
        return resp?.result ?? {};
    }
}

// ── MCP initialisation ────────────────────────────────────────────────────────

async function initMcp(configPath) {
    if (!configPath || !fs.existsSync(configPath)) {
        log(`No MCP config at '${configPath ?? 'none'}' — starting without MCP tools`);
        return;
    }

    let config;
    try {
        config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    } catch (err) {
        log(`WARN: failed to parse MCP config at ${configPath}: ${err.message}`);
        return;
    }

    const servers = config.mcpServers || {};
    let totalTools = 0;

    for (const [serverName, cfg] of Object.entries(servers)) {
        if (!cfg.url) {
            log(`Skipping MCP server '${serverName}': no URL (stdio transport not supported)`);
            continue;
        }

        const client = new McpHttpClient(serverName, cfg.url);
        try {
            await client.initialize();
            const tools = await client.listTools();

            const functionDeclarations = [];
            for (const tool of tools) {
                functionDeclarations.push({
                    name: tool.name,
                    description: tool.description || '',
                    // MCP inputSchema is a JSON Schema object; Gemini expects the same shape
                    parameters: tool.inputSchema || { type: 'object', properties: {} },
                });
                toolServerMap[tool.name] = serverName;
            }

            mcpServers[serverName] = { client, functionDeclarations };
            totalTools += tools.length;
            log(`MCP server '${serverName}': ${tools.length} tool(s) registered`);
        } catch (err) {
            log(`WARN: failed to initialise MCP server '${serverName}': ${err.message}`);
        }
    }

    log(`MCP ready: ${Object.keys(mcpServers).length} server(s), ${totalTools} total tool(s)`);
}

// ── MCP tool dispatch ─────────────────────────────────────────────────────────

async function callMcpTool(toolName, args) {
    const serverName = toolServerMap[toolName];
    if (!serverName || !mcpServers[serverName]) {
        throw new Error(`No MCP server registered for tool '${toolName}'`);
    }
    const result = await mcpServers[serverName].client.callTool(toolName, args);
    // MCP tool result: { content: [{type, text}], isError? }
    const text = Array.isArray(result.content)
        ? result.content.map(c => c.text ?? '').join('')
        : JSON.stringify(result);
    return { result: text };
}

// ── Chat session management ───────────────────────────────────────────────────

function allFunctionDeclarations() {
    return Object.values(mcpServers).flatMap(s => s.functionDeclarations);
}

function ensureChat(systemPrompt, model) {
    if (chat && systemPrompt === currentSystemPrompt && model === currentModel) {
        return; // reuse existing session — history accumulates naturally
    }

    const decls = allFunctionDeclarations();
    const config = {};
    if (systemPrompt) config.systemInstruction = systemPrompt;
    if (decls.length > 0) config.tools = [{ functionDeclarations: decls }];

    chat = ai.chats.create({ model, config });
    currentSystemPrompt = systemPrompt;
    currentModel = model;

    log(`New chat session (model=${model}, tools=${decls.length}, ` +
        `systemPrompt=${systemPrompt ? systemPrompt.length + ' chars' : 'none'})`);
}

// ── Task execution ────────────────────────────────────────────────────────────

async function runTask(msg) {
    const startMs = Date.now();
    const model = msg.model || 'gemini-2.5-flash';
    const systemPrompt = msg.systemPrompt || '';

    try {
        // Lazy-init GoogleGenAI on first task so startup is fast.
        // @google/genai accepts an auth object with getAccessToken() — OAuth2Client
        // from google-auth-library satisfies this interface and handles silent refresh.
        if (!ai) {
            oauth2Client = initAuth();
            ai = new GoogleGenAI({ auth: oauth2Client });
        }

        ensureChat(systemPrompt, model);

        emit({ type: 'ack' });
        emit({ type: 'turn.started' });

        // Build user message parts (text + optional inline attachments)
        const parts = [];

        if (msg.prompt) parts.push({ text: msg.prompt });

        if (Array.isArray(msg.attachments)) {
            for (const att of msg.attachments) {
                if (!att.path || !fs.existsSync(att.path)) {
                    log(`WARN: attachment not found at '${att.path}' — skipped`);
                    continue;
                }
                try {
                    const data = fs.readFileSync(att.path).toString('base64');
                    parts.push({ inlineData: { mimeType: att.mimeType || 'application/octet-stream', data } });
                } catch (err) {
                    log(`WARN: failed to read attachment '${att.path}': ${err.message}`);
                }
            }
        }

        if (parts.length === 0) parts.push({ text: '' }); // model requires at least one part

        let fullText = '';
        let finalUsage = null;

        await runConversationTurn(
            parts,
            (textChunk) => { fullText += textChunk; },
            (usage)     => { finalUsage = usage; }
        );

        emit({
            type: 'turn.completed',
            text: fullText,
            usage: finalUsage,
            durationMs: Date.now() - startMs,
        });
    } catch (err) {
        emit({ type: 'turn.failed', error: err.message ?? String(err) });
    }
}

// Runs one model turn, handles function call round-trips recursively.
// Calls onText(chunk) for each text delta, onUsage(meta) once with final usage.
async function runConversationTurn(messageParts, onText, onUsage) {
    // @google/genai sendMessageStream accepts: string | Part | Part[]
    const input = messageParts.length === 1 && 'text' in messageParts[0]
        ? messageParts[0].text   // plain string shortcut for text-only messages
        : messageParts;

    const stream = await chat.sendMessageStream(input);

    let lastChunk = null;
    for await (const chunk of stream) {
        lastChunk = chunk;
        const text = chunk.text;
        if (text) {
            onText(text);
            emit({ type: 'item.started', itemType: 'message', text });
        }
    }

    // Extract usage from the last chunk (Gemini sends usageMetadata on the final chunk)
    const usageMeta = lastChunk?.usageMetadata;
    if (usageMeta) {
        onUsage({
            inputTokens:  usageMeta.promptTokenCount     ?? 0,
            outputTokens: usageMeta.candidatesTokenCount ?? 0,
        });
    }

    // Handle function calls: execute via MCP, feed results back, recurse
    const functionCalls = lastChunk?.functionCalls?.() ?? [];
    if (functionCalls.length === 0) return;

    const responseParts = [];
    for (const fc of functionCalls) {
        emit({
            type: 'item.started',
            itemType: 'tool_use',
            toolName: fc.name,
            toolArgs: JSON.stringify(fc.args ?? {}),
        });

        let resultObj;
        try {
            resultObj = await callMcpTool(fc.name, fc.args ?? {});
        } catch (err) {
            resultObj = { error: err.message };
            log(`WARN: tool '${fc.name}' failed: ${err.message}`);
        }

        emit({
            type: 'item.completed',
            itemType: 'tool_result',
            text: typeof resultObj.result === 'string'
                ? resultObj.result
                : JSON.stringify(resultObj),
        });

        responseParts.push({
            functionResponse: { name: fc.name, response: resultObj },
        });
    }

    // Send tool results back to the model for the next turn
    await runConversationTurn(responseParts, onText, onUsage);
}

// ── Entry point ───────────────────────────────────────────────────────────────

async function main() {
    log(`Starting (pid=${process.pid}, mcp-config=${mcpConfigPath ?? 'none'})`);

    // Initialise MCP (non-fatal — agent runs without tools if MCP servers are unreachable)
    await initMcp(mcpConfigPath);

    const rl = readline.createInterface({ input: process.stdin, terminal: false });

    rl.on('line', async (line) => {
        const trimmed = line.trim();
        if (!trimmed) return;

        let msg;
        try {
            msg = JSON.parse(trimmed);
        } catch {
            emit({ type: 'error', message: `Invalid JSON on stdin: ${trimmed.slice(0, 100)}` });
            return;
        }

        if (msg.type === 'task' || msg.type === 'command') {
            await runTask(msg);
        }
    });

    rl.on('close', () => {
        log('stdin closed — exiting');
        process.exit(0);
    });
}

main().catch((err) => {
    process.stderr.write(`[gemini-bridge] Fatal: ${err.message}\n${err.stack ?? ''}\n`);
    process.exit(1);
});
