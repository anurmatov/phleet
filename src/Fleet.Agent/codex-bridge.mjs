#!/usr/bin/env node
/**
 * codex-bridge.mjs — persistent bridge between .NET CodexExecutor and @openai/codex-sdk.
 *
 * Protocol:
 *   stdin:  one JSON line per task/command from .NET
 *     {"type":"task","prompt":"...","systemPrompt":"...","model":"gpt-5","sessionId":null}
 *     {"type":"command","prompt":"...","sessionId":"<threadId>"}
 *   stdout: JSONL events streamed back
 *     {"type":"ack","sessionId":"<threadId>"}
 *     {"type":"thread.started"}
 *     {"type":"turn.started"}
 *     {"type":"item.started","itemType":"message","text":"..."}
 *     {"type":"item.started","itemType":"tool_use","toolName":"...","toolArgs":"..."}
 *     {"type":"item.completed","itemType":"tool_result","text":"..."}
 *     {"type":"turn.completed","sessionId":"<threadId>","text":"...","usage":{...},"durationMs":0}
 *     {"type":"turn.failed","error":"..."}
 *     {"type":"error","message":"..."}
 */

import { Codex } from '@openai/codex-sdk';
import readline from 'readline';
import path from 'path';
import fs from 'fs';

// Auth: seed from host mount if local auth not present
const AUTH_PATH = path.join(process.env.HOME || '/root', '.codex', 'auth.json');
const HOST_AUTH_PATH = '/root/.codex-host/auth.json';
if (!fs.existsSync(AUTH_PATH) && fs.existsSync(HOST_AUTH_PATH)) {
    fs.mkdirSync(path.dirname(AUTH_PATH), { recursive: true });
    fs.copyFileSync(HOST_AUTH_PATH, AUTH_PATH);
}

// Parse --mcp-config argument
let mcpConfigPath = null;
for (let i = 2; i < process.argv.length; i++) {
    if (process.argv[i] === '--mcp-config' && process.argv[i + 1]) {
        mcpConfigPath = process.argv[i + 1];
        break;
    }
}

// Current thread ID for resuming across tasks
let currentThreadId = null;

function emit(obj) {
    process.stdout.write(JSON.stringify(obj) + '\n');
}

async function runTask(msg) {
    const startMs = Date.now();
    const VALID_CODEX_MODELS = new Set(['gpt-5', 'gpt-5.4', 'gpt-5.4-mini', 'gpt-5.3-codex', 'codex-mini-latest']);
    const model = (msg.model && VALID_CODEX_MODELS.has(msg.model)) ? msg.model : undefined;
    const sessionId = msg.sessionId || currentThreadId;

    // Codex SDK reads system instructions from AGENTS.md in the work directory
    if (msg.systemPrompt) {
        fs.writeFileSync('/workspace/AGENTS.md', msg.systemPrompt);
    }

    const codex = new Codex();

    let finalText = '';
    let usage = null;
    let threadId = sessionId;

    try {
        const sandboxMode = process.env.CODEX_SANDBOX_MODE || 'danger-full-access';
        const threadOpts = { skipGitRepoCheck: true, sandboxMode, ...(model && { model }), workingDirectory: '/workspace' };
        const thread = sessionId
            ? codex.resumeThread(sessionId, threadOpts)
            : codex.startThread(threadOpts);
        const streamed = await thread.runStreamed(msg.prompt);

        for await (const event of streamed.events) {
            const evType = event.type ?? '';

            if (evType === 'thread.started') {
                threadId = event.thread_id ?? threadId;
                currentThreadId = threadId;
                emit({ type: 'thread.started' });
                emit({ type: 'ack', sessionId: threadId });
            } else if (evType === 'turn.started') {
                emit({ type: 'turn.started' });
            } else if (evType === 'item.started') {
                const item = event.item ?? {};
                if (item.type === 'agent_message') {
                    emit({ type: 'item.started', itemType: 'message', text: item.text ?? '' });
                } else if (item.type === 'command_execution') {
                    emit({
                        type: 'item.started',
                        itemType: 'tool_use',
                        toolName: item.command ?? '',
                        toolArgs: '{}',
                    });
                } else if (item.type === 'mcp_tool_call') {
                    emit({
                        type: 'item.started',
                        itemType: 'tool_use',
                        toolName: item.tool ?? '',
                        toolArgs: JSON.stringify(item.arguments ?? {}),
                    });
                }
            } else if (evType === 'item.completed') {
                const item = event.item ?? {};
                if (item.type === 'agent_message') {
                    finalText = item.text ?? '';
                } else if (item.type === 'command_execution') {
                    emit({ type: 'item.completed', itemType: 'tool_result', text: item.aggregated_output ?? '' });
                } else if (item.type === 'mcp_tool_call') {
                    const resultText = item.result?.content?.map(c => c.text ?? '').join('') ?? item.error?.message ?? '';
                    emit({ type: 'item.completed', itemType: 'tool_result', text: resultText });
                }
            } else if (evType === 'turn.completed') {
                usage = event.usage ?? null;
                emit({
                    type: 'turn.completed',
                    sessionId: threadId,
                    text: finalText,
                    usage: usage ? {
                        input_tokens: usage.input_tokens ?? 0,
                        output_tokens: usage.output_tokens ?? 0,
                    } : null,
                    durationMs: Date.now() - startMs,
                });
            } else if (evType === 'turn.failed') {
                emit({ type: 'turn.failed', error: event.error?.message ?? event.error ?? 'Turn failed' });
                return;
            }
        }
    } catch (err) {
        emit({ type: 'error', message: err.message ?? String(err) });
    }
}

function extractText(item) {
    if (typeof item.content === 'string') return item.content;
    if (Array.isArray(item.content)) {
        return item.content
            .filter(c => c.type === 'text')
            .map(c => c.text ?? '')
            .join('');
    }
    return '';
}

// Read JSONL from stdin, one message at a time (sequential)
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

    if (msg.type === 'task' || msg.type === 'command') {
        await runTask(msg);
    }
});

rl.on('close', () => process.exit(0));
