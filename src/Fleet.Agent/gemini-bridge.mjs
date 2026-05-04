#!/usr/bin/env node
/**
 * gemini-bridge.mjs — persistent bridge between .NET GeminiExecutor and @google/gemini-cli-core.
 *
 * Protocol (stdin→stdout NDJSON):
 *   stdin:  {"type":"task","prompt":"...","model":"gemini-2.5-flash","sessionId":null}
 *           {"type":"task","prompt":"...","images":[{"mimeType":"image/jpeg","base64Data":"..."}]}
 *           {"type":"command","prompt":"/compact","sessionId":"<sessionId>"}
 *   stdout: {"type":"ack","sessionId":"<uuid>"}
 *           {"type":"turn.started"}
 *           {"type":"item.started","itemType":"message","text":"..."}
 *           {"type":"item.started","itemType":"tool_use","toolName":"...","toolArgs":"..."}
 *           {"type":"item.completed","itemType":"tool_result","text":"..."}
 *           {"type":"turn.completed","sessionId":"<uuid>","text":"...","usage":null,"durationMs":0}
 *           {"type":"turn.failed","error":"..."}
 *           {"type":"error","message":"..."}
 *
 * System prompt: GEMINI_SYSTEM_MD file → Config.userMemory → getCoreSystemPrompt().
 * API key: GEMINI_API_KEY env var, passed in-memory only — never written to disk.
 * MCP: HTTP/SSE servers from --mcp-config; stdio entries are skipped with stderr warning.
 * Images: content parts with type='media' (LegacyAgentProtocol contentPartsToGeminiParts format).
 * PDFs: hint-only ([document attachment: path] in task text) — no inline document data in v1.
 *
 * @google/gemini-cli-core API used:
 *   - LegacyAgentProtocol — streaming conversation loop with tool handling via Scheduler
 *   - Config — client configuration, userMemory = system prompt
 *   - MCPServerConfig — HTTP/SSE MCP server descriptors
 *   - ApprovalMode — YOLO mode suppresses PolicyEngine ASK_USER in headless environments
 */

// NOTE: This bridge uses LegacyAgentProtocol rather than LocalAgentExecutor as originally
// specified. LocalAgentExecutor.run() is a one-shot call that blocks until the agent finishes
// and does not expose a subscribe/send streaming API. LegacyAgentProtocol provides subscribe()
// for streaming events and send() for injecting messages, which maps directly onto the bridge's
// NDJSON protocol (ack → turn.started → item.* → turn.completed). The deviation is intentional.

import {
  LegacyAgentProtocol,
  Config,
  MCPServerConfig,
  AuthType,
  ApprovalMode,
} from '@google/gemini-cli-core';
import readline from 'readline';
import fs from 'fs';
import { randomUUID } from 'crypto';
import {
  extractAgentMessageText,
  parseRetryDelayMs,
  MAX_RETRY_DELAY_MS,
} from './gemini-message-handler.mjs';

// ── GEMINI_API_KEY guard ─────────────────────────────────────────────────────

if (!process.env.GEMINI_API_KEY) {
  process.stdout.write(JSON.stringify({ type: 'error', message: 'GEMINI_API_KEY is required' }) + '\n');
  process.exit(1);
}

// ── System prompt ────────────────────────────────────────────────────────────

const systemPromptPath = process.env.GEMINI_SYSTEM_MD;
if (!systemPromptPath) {
  process.stdout.write(JSON.stringify({ type: 'error', message: 'GEMINI_SYSTEM_MD is required' }) + '\n');
  process.exit(1);
}

let systemPromptText;
try {
  systemPromptText = fs.readFileSync(systemPromptPath, 'utf8');
} catch (e) {
  process.stdout.write(JSON.stringify({ type: 'error', message: `GEMINI_SYSTEM_MD file not readable: ${systemPromptPath}` }) + '\n');
  process.exit(1);
}

// ── MCP config ───────────────────────────────────────────────────────────────

let mcpConfigPath = null;
for (let i = 2; i < process.argv.length; i++) {
  if (process.argv[i] === '--mcp-config' && process.argv[i + 1]) {
    mcpConfigPath = process.argv[i + 1];
    break;
  }
}

/**
 * Wraps MCPServerConfig's positional constructor with named parameters so the intent of
 * each argument slot is clear at the call site. MCPServerConfig(command, args, env, cwd,
 * url, httpUrl, headers, tcp, type, ...) — only url and type are used for HTTP/SSE servers.
 */
function makeMcpServerConfig(url, type) {
  return new MCPServerConfig(
    /* command  */ undefined,
    /* args     */ undefined,
    /* env      */ undefined,
    /* cwd      */ undefined,
    /* url      */ url,
    /* httpUrl  */ undefined,
    /* headers  */ undefined,
    /* tcp      */ undefined,
    /* type     */ type,
  );
}

const httpMcpServers = {};
if (mcpConfigPath && fs.existsSync(mcpConfigPath)) {
  try {
    const mcpCfg = JSON.parse(fs.readFileSync(mcpConfigPath, 'utf8'));
    const servers = mcpCfg.mcpServers || {};
    for (const [name, s] of Object.entries(servers)) {
      if (!s.url) {
        process.stderr.write(`Gemini bridge: stdio-transport MCP server '${name}' skipped — only HTTP/SSE transport supported\n`);
        continue;
      }
      const type = s.transport_type === 'sse' ? 'sse' : 'http';
      httpMcpServers[name] = makeMcpServerConfig(s.url, type);
    }
  } catch (e) {
    process.stderr.write(`Gemini bridge: failed to parse MCP config at ${mcpConfigPath}: ${e.message}\n`);
  }
}

// ── Model validation ─────────────────────────────────────────────────────────

// Mirror codex-bridge's VALID_CODEX_MODELS pattern. Unknown model names fall back to
// the default rather than passing an arbitrary string to the Gemini SDK, which would
// produce an opaque SDK error instead of a safe fallback.
//
// IMPORTANT: keep this list in sync with the gemini array in
// src/fleet-dashboard/src/constants.ts — mismatches cause silent fallback.
const VALID_GEMINI_MODELS = new Set([
  'gemini-3.1-pro-preview',
  'gemini-3-flash-preview',
  'gemini-3.1-flash-lite-preview',
  'gemini-2.5-pro',
  'gemini-2.5-flash',
  'gemini-2.5-flash-lite',
]);
const DEFAULT_GEMINI_MODEL = 'gemini-2.5-flash';

// ── Rate-limit retry ─────────────────────────────────────────────────────────

const RATE_LIMIT_RE = /429|rate.?limit|resource.?exhausted|quota.?exceeded/i;
const MAX_RETRIES = 3;

/**
 * Extracts the retryDelay value string from a Gemini 429 error message.
 * Returns null if no retryDelay field is found (caller uses DEFAULT_RETRY_DELAY_MS).
 */
function extractRetryDelay(errMessage) {
  const m = /retry.?delay[^a-z0-9"]*"?(\d+m\d+s|\d+m|\d+s)/i.exec(errMessage ?? '');
  return m ? m[1] : null;
}

// ── Session state ────────────────────────────────────────────────────────────

// Config and protocol are created once on first task and reused.
// LegacyAgentProtocol maintains conversation history internally via its GeminiClient.
let config = null;
let protocol = null;
let currentSessionId = null;
let initError = null;

function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

async function ensureInitialized(model) {
  if (config) return; // Config is created once and reused for the bridge process lifetime.
  // NOTE: the model argument is only honored on the FIRST call. Subsequent tasks that
  // arrive with a different model value are silently ignored — the bridge would need to
  // restart to pick up a different model. This matches the codex-bridge pattern.
  if (initError) throw initError;

  const hasMcp = Object.keys(httpMcpServers).length > 0;

  config = new Config({
    sessionId: randomUUID(),
    model: model || DEFAULT_GEMINI_MODEL,
    targetDir: process.cwd(),
    // userMemory → getSystemInstructionMemory() → getCoreSystemPrompt() = system instruction
    userMemory: systemPromptText,
    mcpServers: hasMcp ? httpMcpServers : undefined,
    mcpEnabled: hasMcp,
    extensionsEnabled: false,
    checkpointing: false,
    debugMode: false,
    coreTools: [],
    // YOLO: suppress PolicyEngine ASK_USER in headless mode. Without this, every MCP tool call
    // is evaluated as requiring user confirmation; the MessageBus has no UI listener in a
    // non-TTY environment, so calls short-circuit to { confirmed: false } and never execute.
    approvalMode: ApprovalMode.YOLO,
  });

  try {
    await config.initialize();
    // refreshAuth creates config.contentGenerator — required before any generateContent call.
    // initialize() alone leaves contentGenerator null; LegacyAgentProtocol.send() throws
    // 'Content generator not initialized' without this step.
    await config.refreshAuth(AuthType.USE_GEMINI, process.env.GEMINI_API_KEY);
  } catch (e) {
    initError = e;
    config = null;
    throw e;
  }

  protocol = new LegacyAgentProtocol({
    config,
    promptId: 'fleet-bridge',
  });
}

/**
 * Builds the content-parts array for a single task message (reused across retries).
 */
function buildContent(msg) {
  const content = [];
  if (Array.isArray(msg.images)) {
    for (const img of msg.images) {
      if (img?.mimeType && img?.base64Data) {
        content.push({ type: 'media', data: img.base64Data, mimeType: img.mimeType });
      } else {
        process.stderr.write('Gemini bridge: image entry missing mimeType or base64Data — skipped\n');
      }
    }
  }
  content.push({ type: 'text', text: msg.prompt || '' });
  return content;
}

/**
 * Runs a single send attempt against the already-initialized protocol.
 *
 * Returns one of:
 *   { success: true,        text, durationMs }
 *   { rateLimited: true,    errorMsg }       — caller should wait and retry
 *   { failed: true,         errorMsg }       — non-retryable; caller should emit turn.failed
 */
function runSingleAttempt(content, sessionId, startMs) {
  return new Promise((resolve) => {
    let finalText = '';

    // Subscribe BEFORE send() so we don't miss agent_start.
    const unsubscribe = protocol.subscribe((event) => {
      const evType = event.type;

      if (evType === 'agent_start') {
        emit({ type: 'turn.started' });
      } else if (evType === 'message') {
        // extractAgentMessageText guards against user-role echo and empty content.
        // See gemini-message-handler.mjs for the role-guard logic and its unit tests.
        const text = extractAgentMessageText(event);
        if (text) {
          finalText += text;
          emit({ type: 'item.started', itemType: 'message', text });
        }
      } else if (evType === 'tool_request') {
        emit({
          type: 'item.started',
          itemType: 'tool_use',
          toolName: event.name ?? '',
          toolArgs: JSON.stringify(event.args ?? {}),
        });
      } else if (evType === 'tool_response') {
        const text = Array.isArray(event.content)
          ? event.content.filter(c => c.type === 'text').map(c => c.text ?? '').join('')
          : '';
        emit({ type: 'item.completed', itemType: 'tool_result', text });
      } else if (evType === 'agent_end') {
        unsubscribe();
        resolve({ success: true, text: finalText, durationMs: Date.now() - startMs });
      } else if (evType === 'error') {
        unsubscribe();
        const errorMsg = event.message ?? event.error?.message ?? 'Unknown error';
        if (RATE_LIMIT_RE.test(errorMsg)) {
          resolve({ rateLimited: true, errorMsg });
        } else {
          resolve({ failed: true, errorMsg });
        }
      }
    });

    // send() is non-blocking — the run loop starts in a macrotask via setTimeout.
    protocol.send({ message: { content } }).catch((err) => {
      unsubscribe();
      const errorMsg = err.message ?? String(err);
      if (RATE_LIMIT_RE.test(errorMsg)) {
        resolve({ rateLimited: true, errorMsg });
      } else {
        resolve({ failed: true, errorMsg });
      }
    });
  });
}

async function runTask(msg) {
  const startMs = Date.now();
  let model = DEFAULT_GEMINI_MODEL;
  if (msg.model && VALID_GEMINI_MODELS.has(msg.model)) {
    model = msg.model;
  } else if (msg.model) {
    process.stderr.write(
      `Gemini bridge: unknown model '${msg.model}' — falling back to ${DEFAULT_GEMINI_MODEL}. ` +
      `Valid models: ${[...VALID_GEMINI_MODELS].join(', ')}\n`,
    );
  }
  const sessionId = msg.sessionId || currentSessionId || randomUUID();
  currentSessionId = sessionId;

  emit({ type: 'ack', sessionId });

  try {
    await ensureInitialized(model);
  } catch (err) {
    emit({ type: 'turn.failed', error: `Initialization failed: ${err.message ?? String(err)}` });
    return;
  }

  // Build content once — reused across rate-limit retries so images aren't re-encoded.
  const content = buildContent(msg);

  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    const result = await runSingleAttempt(content, sessionId, startMs);

    if (result.success) {
      emit({
        type: 'turn.completed',
        sessionId,
        text: result.text,
        usage: null,
        durationMs: result.durationMs,
      });
      return;
    }

    if (result.failed) {
      emit({ type: 'turn.failed', error: result.errorMsg });
      return;
    }

    // rateLimited — wait then retry if attempts remain.
    if (attempt >= MAX_RETRIES) {
      emit({
        type: 'turn.failed',
        error: `Rate limited after ${MAX_RETRIES} attempt(s): ${result.errorMsg}`,
      });
      return;
    }

    const delayMs = Math.min(
      parseRetryDelayMs(extractRetryDelay(result.errorMsg)),
      MAX_RETRY_DELAY_MS,
    );
    process.stderr.write(
      `Gemini bridge: rate limited, waiting ${Math.round(delayMs / 1000)}s before retry ${attempt}/${MAX_RETRIES - 1}\n`,
    );
    await new Promise((r) => setTimeout(r, delayMs));
  }
}

// ── Main loop ────────────────────────────────────────────────────────────────

// LegacyAgentProtocol.send() cannot be called while a prior stream is active.
// A Promise chain serialises back-to-back task arrivals so each waits for the
// previous one to finish before starting.
const rl = readline.createInterface({ input: process.stdin, terminal: false });

let _taskQueue = Promise.resolve();

rl.on('line', (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;

  let msg;
  try {
    msg = JSON.parse(trimmed);
  } catch {
    emit({ type: 'error', message: `Invalid JSON: ${trimmed}` });
    return;
  }

  if (msg.type === 'task' || msg.type === 'command') {
    // Chain onto the previous task so they run serially, never concurrently.
    _taskQueue = _taskQueue.then(() => runTask(msg).catch((err) => {
      emit({ type: 'error', message: err.message ?? String(err) });
    }));
  }
});

rl.on('close', () => process.exit(0));
