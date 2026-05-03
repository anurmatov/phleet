#!/usr/bin/env node
/**
 * gemini-bridge.mjs — persistent bridge between .NET GeminiExecutor and @google/gemini-cli-core.
 *
 * Protocol:
 *   stdin:  one JSON line per task/command from .NET
 *     {"type":"task","prompt":"...","model":"gemini-2.5-flash","sessionId":null}
 *     {"type":"task","prompt":"...","images":[{"mimeType":"image/jpeg","base64Data":"..."}]}
 *     {"type":"command","prompt":"/compact","sessionId":"<sessionId>"}
 *   stdout: NDJSON events streamed back
 *     {"type":"ack","sessionId":"<uuid>"}
 *     {"type":"turn.started"}
 *     {"type":"item.started","itemType":"message","text":"..."}
 *     {"type":"item.started","itemType":"tool_use","toolName":"...","toolArgs":"..."}
 *     {"type":"item.completed","itemType":"tool_result","text":"..."}
 *     {"type":"turn.completed","sessionId":"<uuid>","text":"...","usage":{...},"durationMs":0}
 *     {"type":"turn.failed","error":"..."}
 *     {"type":"error","message":"..."}
 *
 * System prompt is delivered exclusively via GEMINI_SYSTEM_MD env var (file path).
 * It does NOT appear in the stdin task message — see GeminiExecutor.StartProcessAsync().
 *
 * MCP config is accepted via --mcp-config <path> (same as codex-bridge.mjs).
 * Only HTTP/SSE transport servers are registered; stdio entries are logged and skipped.
 *
 * PDFs: hint-only mode. GeminiExecutor injects [document attachment: path] into the
 * task text (same as CodexExecutor). No inline document data for PDFs in v1.
 */

import { GeminiCliAgent, LocalAgentDefinition, ToolRegistry, MessageBus, Config } from '@google/gemini-cli-core';
import readline from 'readline';
import fs from 'fs';
import { randomUUID } from 'crypto';

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

const mcpServersConfig = {};
if (mcpConfigPath && fs.existsSync(mcpConfigPath)) {
  try {
    const mcpCfg = JSON.parse(fs.readFileSync(mcpConfigPath, 'utf8'));
    const servers = mcpCfg.mcpServers || {};
    for (const [name, s] of Object.entries(servers)) {
      if (!s.url) {
        process.stderr.write(`Gemini bridge: stdio-transport MCP server '${name}' skipped — only HTTP/SSE transport supported\n`);
        continue;
      }
      mcpServersConfig[name] = s;
    }
  } catch (e) {
    process.stderr.write(`Gemini bridge: failed to parse MCP config at ${mcpConfigPath}: ${e.message}\n`);
  }
}

// ── Session state ────────────────────────────────────────────────────────────

// Maintain conversation history across tasks for in-process continuity.
// GeminiCliAgent has no resumeThread() — we keep history and reconstruct per task.
let conversationHistory = [];
let currentSessionId = null;

function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

async function runTask(msg) {
  const startMs = Date.now();
  const model = msg.model || 'gemini-2.5-flash';
  const sessionId = msg.sessionId || currentSessionId || randomUUID();
  currentSessionId = sessionId;

  emit({ type: 'ack', sessionId });
  emit({ type: 'turn.started' });

  let finalText = '';

  try {
    const config = new Config({ apiKey: process.env.GEMINI_API_KEY });
    const registry = new ToolRegistry({ mcpServers: mcpServersConfig });

    const def = new LocalAgentDefinition({
      model,
      systemInstruction: systemPromptText,
      tools: registry,
      history: conversationHistory,
    });

    // Build content parts: text + optional images
    const contentParts = [];

    // Add images if present
    const images = msg.images;
    if (Array.isArray(images) && images.length > 0) {
      for (const img of images) {
        if (img && img.mimeType && img.base64Data) {
          contentParts.push({
            inlineData: { mimeType: img.mimeType, data: img.base64Data },
          });
        } else {
          process.stderr.write('Gemini bridge: image entry missing mimeType or base64Data — skipped\n');
        }
      }
    }

    // Text part always last
    contentParts.push({ text: msg.prompt || '' });

    // Determine the prompt to pass to agent.run()
    // If multipart (images present), pass parts array; otherwise plain string
    const prompt = contentParts.length > 1 ? contentParts : (msg.prompt || '');

    const agent = new GeminiCliAgent(def, config, new MessageBus());

    for await (const event of agent.run(prompt)) {
      const evType = event.type ?? '';

      if (evType === 'thought' || evType === 'thinking') {
        // Gemini thinking steps — skip, not forwarded
        continue;
      } else if (evType === 'text' || evType === 'message' || evType === 'content') {
        const text = event.text ?? event.content ?? '';
        if (text) {
          finalText += text;
          emit({ type: 'item.started', itemType: 'message', text });
        }
      } else if (evType === 'tool_call' || evType === 'tool_use' || evType === 'function_call') {
        emit({
          type: 'item.started',
          itemType: 'tool_use',
          toolName: event.toolName ?? event.name ?? '',
          toolArgs: JSON.stringify(event.args ?? event.arguments ?? {}),
        });
      } else if (evType === 'tool_result' || evType === 'tool_response' || evType === 'function_response') {
        const resultText = typeof event.result === 'string'
          ? event.result
          : JSON.stringify(event.result ?? '');
        emit({ type: 'item.completed', itemType: 'tool_result', text: resultText });
      } else if (evType === 'turn_complete' || evType === 'turn.complete' || evType === 'complete') {
        finalText = event.text ?? finalText;
        const usage = event.usage ?? null;
        // Update history for next task
        if (event.history) {
          conversationHistory = event.history;
        }
        emit({
          type: 'turn.completed',
          sessionId,
          text: finalText,
          usage: usage ? {
            input_tokens: usage.inputTokenCount ?? usage.input_tokens ?? 0,
            output_tokens: usage.outputTokenCount ?? usage.output_tokens ?? 0,
          } : null,
          durationMs: Date.now() - startMs,
        });
        return;
      } else if (evType === 'error') {
        emit({ type: 'turn.failed', error: event.message ?? event.error ?? 'Unknown error' });
        return;
      }
    }

    // If we reach here without a turn_complete event, emit turn.completed
    emit({
      type: 'turn.completed',
      sessionId,
      text: finalText,
      usage: null,
      durationMs: Date.now() - startMs,
    });
  } catch (err) {
    emit({ type: 'turn.failed', error: err.message ?? String(err) });
  }
}

// ── Main loop ────────────────────────────────────────────────────────────────

const rl = readline.createInterface({ input: process.stdin, terminal: false });

rl.on('line', async (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;

  let msg;
  try {
    msg = JSON.parse(trimmed);
  } catch {
    emit({ type: 'error', message: `Invalid JSON: ${trimmed}` });
    return;
  }

  try {
    if (msg.type === 'task' || msg.type === 'command') {
      await runTask(msg);
    }
  } catch (err) {
    emit({ type: 'error', message: err.message ?? String(err) });
  }
});

rl.on('close', () => process.exit(0));
