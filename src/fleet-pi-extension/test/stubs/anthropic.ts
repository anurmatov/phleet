// Scripted Anthropic Messages stub for the Fleet Pi extension tests (phleet #386 §15).
//
// CLI (shared with the C# RealPi tests):
//   node test/stubs/anthropic.ts --record-dir <dir> --script <file.json>
// prints `LISTENING http://127.0.0.1:<port>` as its first stdout line and serves
// POST /v1/messages (any query string). Each request is recorded as
// `<dir>/<NNNN>.json` = {"headers":{...},"body":{...}}, NNNN counting from 0001.
//
// Script: {"main":[step,...],"summary":[step,...],"default_summary":[step,...]}.
// A request is a `summary` request when the last user message's text starts with
// "Do not call tools."; it is a `default_summary` request when its system prompt
// is Pi's own summarization prompt; every other request is `main`. Each list is
// consumed in order; an exhausted list answers {"text":"OK"} (default_summary:
// {"text":"Default summary."}).
//
// Steps: {"text":"..."} | {"tool_use":{"name":"bash","input":{...}}} |
// {"overflow400":{"n_prompt":140000,"n_ctx":131072}} | {"status":500} |
// {"max_tokens":"text"} | {"empty":true}; any step may add {"delay_ms":N} and
// {"usage":{"input":N,"cache_read":N,"output":N}}. A step may combine "text" and
// "tool_use". Streaming SSE when the body has "stream":true, JSON otherwise.

import { mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

export interface StubUsage {
	input?: number;
	cache_read?: number;
	output?: number;
}

export interface StubStep {
	text?: string;
	tool_use?: { name: string; input?: unknown };
	overflow400?: { n_prompt: number; n_ctx: number };
	status?: number;
	max_tokens?: string;
	empty?: boolean;
	delay_ms?: number;
	usage?: StubUsage;
}

export interface StubScript {
	main?: StubStep[];
	summary?: StubStep[];
	default_summary?: StubStep[];
}

export type RequestKind = "main" | "summary" | "default_summary";

export interface RecordedRequest {
	headers: Record<string, string | string[] | undefined>;
	body: any;
}

export const SUMMARY_PREFIX = "Do not call tools.";
/** First words of Pi 1.0.4's SUMMARIZATION_SYSTEM_PROMPT (coding-agent/src/core/compaction/utils.ts). */
export const DEFAULT_SUMMARY_MARKER = "You are a context summarization assistant";
export const DEFAULT_USAGE_INPUT = 100;

/** The exact DwarfStar context-overflow body (phleet #386 §2.2, A20). */
export function overflowBody(nPrompt: number, nCtx: number): string {
	return JSON.stringify({
		type: "error",
		error: {
			type: "invalid_request_error",
			message: `Prompt has ${nPrompt} tokens, but the configured context size is ${nCtx} tokens`,
			n_prompt_tokens: nPrompt,
			n_ctx: nCtx,
		},
	});
}

function systemText(system: unknown): string {
	if (typeof system === "string") return system;
	if (Array.isArray(system)) return system.map((b) => (b && typeof b.text === "string" ? b.text : "")).join("\n");
	return "";
}

/** Text of the last text block of the last message, when that message is a user message. */
export function lastUserText(body: any): string | undefined {
	const messages = Array.isArray(body?.messages) ? body.messages : [];
	const last = messages[messages.length - 1];
	if (!last || last.role !== "user") return undefined;
	if (typeof last.content === "string") return last.content;
	if (!Array.isArray(last.content)) return undefined;
	const texts = last.content.filter((b: any) => b?.type === "text" && typeof b.text === "string");
	return texts.length > 0 ? texts[texts.length - 1].text : undefined;
}

export function classifyRequest(body: any): RequestKind {
	if (systemText(body?.system).includes(DEFAULT_SUMMARY_MARKER)) return "default_summary";
	if (lastUserText(body)?.startsWith(SUMMARY_PREFIX)) return "summary";
	return "main";
}

const FALLBACK_STEP: Record<RequestKind, StubStep> = {
	main: { text: "OK" },
	summary: { text: "OK" },
	default_summary: { text: "Default summary." },
};

function estimateOutputTokens(text: string): number {
	return Math.max(1, Math.ceil(text.length / 4));
}

interface BuiltMessage {
	content: any[];
	stopReason: string;
	usage: { input_tokens: number; cache_read_input_tokens: number; cache_creation_input_tokens: number; output_tokens: number };
}

function buildMessage(step: StubStep, seq: number): BuiltMessage {
	const content: any[] = [];
	let stopReason = "end_turn";
	let outChars = 0;
	if (typeof step.max_tokens === "string") {
		content.push({ type: "text", text: step.max_tokens });
		outChars += step.max_tokens.length;
		stopReason = "max_tokens";
	} else if (!step.empty) {
		if (typeof step.text === "string") {
			content.push({ type: "text", text: step.text });
			outChars += step.text.length;
		}
		if (step.tool_use) {
			const input = step.tool_use.input ?? {};
			content.push({ type: "tool_use", id: `toolu_stub_${seq}_${content.length}`, name: step.tool_use.name, input });
			outChars += JSON.stringify(input).length;
			stopReason = "tool_use";
		}
	}
	const usage = {
		input_tokens: step.usage?.input ?? DEFAULT_USAGE_INPUT,
		cache_read_input_tokens: step.usage?.cache_read ?? 0,
		cache_creation_input_tokens: 0,
		output_tokens: step.usage?.output ?? estimateOutputTokens("x".repeat(outChars)),
	};
	return { content, stopReason, usage };
}

function sse(res: ServerResponse, event: string, data: unknown): void {
	res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
}

function writeStream(res: ServerResponse, model: string, id: string, msg: BuiltMessage): void {
	res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive" });
	sse(res, "message_start", {
		type: "message_start",
		message: {
			id,
			type: "message",
			role: "assistant",
			model,
			content: [],
			stop_reason: null,
			stop_sequence: null,
			usage: { ...msg.usage, output_tokens: 0 },
		},
	});
	sse(res, "ping", { type: "ping" });
	msg.content.forEach((block, index) => {
		if (block.type === "text") {
			sse(res, "content_block_start", { type: "content_block_start", index, content_block: { type: "text", text: "" } });
			if (block.text.length > 0) {
				sse(res, "content_block_delta", { type: "content_block_delta", index, delta: { type: "text_delta", text: block.text } });
			}
		} else {
			sse(res, "content_block_start", {
				type: "content_block_start",
				index,
				content_block: { type: "tool_use", id: block.id, name: block.name, input: {} },
			});
			sse(res, "content_block_delta", {
				type: "content_block_delta",
				index,
				delta: { type: "input_json_delta", partial_json: JSON.stringify(block.input) },
			});
		}
		sse(res, "content_block_stop", { type: "content_block_stop", index });
	});
	sse(res, "message_delta", {
		type: "message_delta",
		delta: { stop_reason: msg.stopReason, stop_sequence: null },
		usage: { output_tokens: msg.usage.output_tokens },
	});
	sse(res, "message_stop", { type: "message_stop" });
	res.end();
}

function writeJson(res: ServerResponse, status: number, body: string): void {
	res.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(body) });
	res.end(body);
}

function readBody(req: IncomingMessage): Promise<string> {
	return new Promise((resolve, reject) => {
		const chunks: Buffer[] = [];
		req.on("data", (c: Buffer) => chunks.push(c));
		req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
		req.on("error", reject);
	});
}

/** Wait `ms`, resolving early (with false) when the client goes away. */
function delay(ms: number, res: ServerResponse): Promise<boolean> {
	return new Promise((resolve) => {
		if (res.destroyed || res.writableEnded) return resolve(false);
		const onClose = () => {
			clearTimeout(timer);
			resolve(false);
		};
		const timer = setTimeout(() => {
			res.off("close", onClose);
			resolve(true);
		}, ms);
		res.once("close", onClose);
	});
}

export interface AnthropicStub {
	url: string;
	port: number;
	recordDir: string;
	/** Requests in arrival order, with the kind the stub assigned. */
	requests: Array<RecordedRequest & { kind: RequestKind; file: string }>;
	close(): Promise<void>;
}

export interface AnthropicStubOptions {
	recordDir: string;
	script: StubScript;
	host?: string;
	port?: number;
	log?: (line: string) => void;
}

export async function startAnthropicStub(options: AnthropicStubOptions): Promise<AnthropicStub> {
	const host = options.host ?? "127.0.0.1";
	mkdirSync(options.recordDir, { recursive: true });
	const queues: Record<RequestKind, StubStep[]> = {
		main: [...(options.script.main ?? [])],
		summary: [...(options.script.summary ?? [])],
		default_summary: [...(options.script.default_summary ?? [])],
	};
	const requests: AnthropicStub["requests"] = [];
	let seq = 0;

	const handle = async (req: IncomingMessage, res: ServerResponse): Promise<void> => {
		// A client that aborted (a summary timeout) must not crash the stub.
		res.on("error", () => undefined);
		const path = new URL(req.url ?? "/", "http://stub").pathname;
		if (req.method !== "POST" || path !== "/v1/messages") {
			writeJson(res, 404, JSON.stringify({ type: "error", error: { type: "not_found_error", message: "not found" } }));
			return;
		}
		const raw = await readBody(req);
		let body: any;
		try {
			body = JSON.parse(raw);
		} catch {
			body = raw;
		}
		seq += 1;
		const n = seq;
		const file = join(options.recordDir, `${String(n).padStart(4, "0")}.json`);
		const recorded: RecordedRequest = { headers: { ...req.headers }, body };
		writeFileSync(file, `${JSON.stringify(recorded, null, 1)}\n`);
		const kind = classifyRequest(body);
		requests.push({ ...recorded, kind, file });
		const step = queues[kind].shift() ?? FALLBACK_STEP[kind];
		options.log?.(`request ${n} kind=${kind}`);

		if (typeof step.delay_ms === "number" && step.delay_ms > 0) {
			if (!(await delay(step.delay_ms, res))) return;
		}
		if (step.overflow400) {
			writeJson(res, 400, overflowBody(step.overflow400.n_prompt, step.overflow400.n_ctx));
			return;
		}
		if (typeof step.status === "number" && step.status >= 400) {
			writeJson(
				res,
				step.status,
				JSON.stringify({ type: "error", error: { type: "api_error", message: `stub status ${step.status}` } }),
			);
			return;
		}
		const msg = buildMessage(step, n);
		const model = typeof body?.model === "string" ? body.model : "stub-model";
		const id = `msg_stub_${String(n).padStart(4, "0")}`;
		if (body?.stream === true) {
			writeStream(res, model, id, msg);
			return;
		}
		writeJson(
			res,
			200,
			JSON.stringify({
				id,
				type: "message",
				role: "assistant",
				model,
				content: msg.content,
				stop_reason: msg.stopReason,
				stop_sequence: null,
				usage: msg.usage,
			}),
		);
	};

	const server: Server = createServer((req, res) => {
		handle(req, res).catch((error: unknown) => {
			options.log?.(`handler error ${error instanceof Error ? error.name : "unknown"}`);
			if (!res.headersSent && !res.destroyed) writeJson(res, 500, JSON.stringify({ type: "error", error: { type: "api_error", message: "stub failure" } }));
			else res.destroy();
		});
	});
	await new Promise<void>((resolve, reject) => {
		server.once("error", reject);
		server.listen(options.port ?? 0, host, () => resolve());
	});
	const port = (server.address() as AddressInfo).port;
	return {
		url: `http://${host}:${port}`,
		port,
		recordDir: options.recordDir,
		requests,
		close: () =>
			new Promise<void>((resolve) => {
				server.closeAllConnections();
				server.close(() => resolve());
			}),
	};
}

function argValue(argv: string[], name: string): string | undefined {
	const i = argv.indexOf(name);
	return i >= 0 ? argv[i + 1] : undefined;
}

function isMain(): boolean {
	const entry = process.argv[1];
	if (!entry) return false;
	try {
		return realpathSync(entry) === realpathSync(fileURLToPath(import.meta.url));
	} catch {
		return false;
	}
}

if (isMain()) {
	const argv = process.argv.slice(2);
	const recordDir = argValue(argv, "--record-dir");
	const scriptPath = argValue(argv, "--script");
	if (!recordDir || !scriptPath) {
		process.stderr.write("usage: anthropic.ts --record-dir <dir> --script <file.json> [--port <n>]\n");
		process.exit(2);
	}
	const script = JSON.parse(readFileSync(scriptPath, "utf8")) as StubScript;
	const port = argValue(argv, "--port");
	const stub = await startAnthropicStub({
		recordDir,
		script,
		port: port ? Number(port) : undefined,
		log: (line) => process.stderr.write(`anthropic-stub: ${line}\n`),
	});
	process.stdout.write(`LISTENING ${stub.url}\n`);
	const shutdown = () => {
		void stub.close().then(() => process.exit(0));
	};
	process.on("SIGTERM", shutdown);
	process.on("SIGINT", shutdown);
}
