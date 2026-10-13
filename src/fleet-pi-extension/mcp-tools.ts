// Fleet MCP bridge (phleet #386 §4.5). Pi's built-in MCP client renames tools
// (`-` → `_`, long names cut with a hash), so Fleet bridges its servers itself:
// every granted tool is registered under its exact name `mcp__<server>__<tool>`.
//
// Discovery runs once, inside the extension's async factory, before the session
// starts: every `http` server with at least one grant is connected in parallel
// (15 s per server, 20 s in total) and the tool list stays fixed for the life of
// the process, so the prompt head never changes. Logs carry names and byte sizes only.

import { readFileSync } from "node:fs";
import type { AgentToolResult, ToolDefinition } from "@earendil-works/pi-coding-agent";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { TOOL_CALL_TIMEOUT_SECONDS, TOOL_RESULT_MAX_BYTES } from "./config.ts";
import type { Logger } from "./log.ts";

export const CONNECT_TIMEOUT_MS = 15_000;
export const DISCOVERY_TIMEOUT_MS = 20_000;
/** Provider tool names are at most 64 characters; Fleet never shortens one. */
export const MAX_TOOL_NAME_LENGTH = 64;
const TOOL_NAME_PATTERN = /^[A-Za-z0-9_-]+$/;

export interface McpServerConfig {
	type?: unknown;
	url?: unknown;
	headers?: unknown;
}

export interface McpBridgeOptions {
	/** Path of the `.mcp.json`-shaped file: {"mcpServers":{name:{type:"http",url,headers?}}}. */
	configPath: string;
	/** Agent.AllowedTools; only exact `mcp__<server>__<tool>` entries matter. */
	allowedTools: readonly string[];
	log: Logger;
	maxBytes?: number;
	callTimeoutMs?: number;
	connectTimeoutMs?: number;
	discoveryTimeoutMs?: number;
	clientName?: string;
}

export interface McpBridge {
	tools: ToolDefinition[];
	/** Names in registration order. */
	names: string[];
	close(): Promise<void>;
}

export function toolName(server: string, tool: string): string {
	return `mcp__${server}__${tool}`;
}

export function truncationMarker(originalBytes: number, maxBytes: number): string {
	return `[fleet-mcp: output truncated from ${originalBytes} to ${maxBytes} bytes]`;
}

/**
 * Cut `text` so the result, marker included, is at most `maxBytes` UTF-8 bytes and
 * ends with the truncation marker. Text within the limit is returned unchanged.
 */
export function limitText(text: string, maxBytes: number): { text: string; truncated: boolean; originalBytes: number } {
	const buffer = Buffer.from(text, "utf8");
	if (buffer.length <= maxBytes) return { text, truncated: false, originalBytes: buffer.length };
	const marker = `\n${truncationMarker(buffer.length, maxBytes)}`;
	let end = Math.max(0, maxBytes - Buffer.byteLength(marker, "utf8"));
	// Back up to a UTF-8 character boundary.
	while (end > 0 && (buffer[end] & 0xc0) === 0x80) end--;
	return { text: buffer.subarray(0, end).toString("utf8") + marker, truncated: true, originalBytes: buffer.length };
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Read the MCP config; undefined (with an ERROR line) when it is missing or malformed. */
export function readMcpConfig(path: string, log: Logger): Record<string, McpServerConfig> | undefined {
	let parsed: unknown;
	try {
		parsed = JSON.parse(readFileSync(path, "utf8"));
	} catch {
		log.error("MCP config missing or malformed (mcpConfigPath); no MCP tools registered");
		return undefined;
	}
	if (!isPlainObject(parsed) || !isPlainObject(parsed.mcpServers)) {
		log.error("MCP config has no mcpServers object (mcpConfigPath); no MCP tools registered");
		return undefined;
	}
	const servers: Record<string, McpServerConfig> = {};
	for (const [name, value] of Object.entries(parsed.mcpServers)) {
		servers[name] = isPlainObject(value) ? value : {};
	}
	return servers;
}

function stringHeaders(value: unknown): Record<string, string> | undefined {
	if (!isPlainObject(value)) return undefined;
	const headers: Record<string, string> = {};
	for (const [k, v] of Object.entries(value)) {
		if (typeof v === "string") headers[k] = v;
	}
	return Object.keys(headers).length > 0 ? headers : undefined;
}

/** MCP input schemas must be objects; some providers reject an object schema without `properties`. */
function toParameters(schema: unknown): Record<string, unknown> {
	const s = isPlainObject(schema) ? schema : {};
	return { ...s, type: s.type ?? "object", ...(s.properties === undefined ? { properties: {} } : {}) };
}

/** Flatten an MCP CallToolResult into the model-facing text. */
export function resultText(result: any): string {
	const blocks: any[] = Array.isArray(result?.content) ? result.content : [];
	if (blocks.length === 0) {
		return result?.structuredContent !== undefined ? JSON.stringify(result.structuredContent) : "";
	}
	return blocks
		.map((block) => {
			if (block?.type === "text" && typeof block.text === "string") return block.text;
			if (block?.type === "resource" && typeof block.resource?.text === "string") return block.resource.text;
			if (block?.type === "resource_link" && typeof block.uri === "string") return `[resource ${block.uri}]`;
			return `[fleet-mcp: ${String(block?.type ?? "unknown")} content omitted]`;
		})
		.join("\n");
}

function errorResult(text: string, details: Record<string, unknown>): AgentToolResult<unknown> & { isError: true } {
	return { content: [{ type: "text", text }], details, isError: true };
}

interface ConnectedServer {
	name: string;
	client: Client;
	tools: Array<{ name: string; description?: string; inputSchema?: unknown }>;
}

async function connectServer(
	name: string,
	url: string,
	headers: Record<string, string> | undefined,
	signal: AbortSignal,
	clientName: string,
): Promise<ConnectedServer> {
	const transport = new StreamableHTTPClientTransport(new URL(url), headers ? { requestInit: { headers } } : undefined);
	const client = new Client({ name: clientName, version: "1.0.0" });
	try {
		await client.connect(transport, { signal });
		const tools: ConnectedServer["tools"] = [];
		let cursor: string | undefined;
		do {
			const page = await client.listTools(cursor ? { cursor } : undefined, { signal });
			tools.push(...page.tools);
			cursor = page.nextCursor;
		} while (cursor);
		return { name, client, tools };
	} catch (error) {
		await client.close().catch(() => undefined);
		throw error;
	}
}

function createToolDefinition(
	server: ConnectedServer,
	tool: ConnectedServer["tools"][number],
	fullName: string,
	maxBytes: number,
	callTimeoutMs: number,
	log: Logger,
): ToolDefinition {
	return {
		name: fullName,
		label: `${server.name}/${tool.name}`,
		description: tool.description?.trim() || `MCP tool ${tool.name} from server ${server.name}`,
		parameters: toParameters(tool.inputSchema) as any,
		async execute(_toolCallId, params, signal) {
			const details = { server: server.name, tool: tool.name };
			if (signal?.aborted) {
				log.warn(`MCP call aborted name=${fullName}`);
				return errorResult(`MCP tool ${fullName} was aborted before it ran`, details);
			}
			const controller = new AbortController();
			const onAbort = () => controller.abort();
			signal?.addEventListener("abort", onAbort, { once: true });
			let timedOut = false;
			const timer = setTimeout(() => {
				timedOut = true;
				controller.abort();
			}, callTimeoutMs);
			try {
				const result = await server.client.callTool(
					{ name: tool.name, arguments: (params ?? {}) as Record<string, unknown> },
					undefined,
					// The SDK's own timeout is a backstop only; ours (and Pi's abort) decide.
					{ signal: controller.signal, timeout: callTimeoutMs + 5_000 },
				);
				const limited = limitText(resultText(result), maxBytes);
				if (limited.truncated) {
					log.warn(`MCP result truncated name=${fullName} bytes=${limited.originalBytes} limit=${maxBytes}`);
				} else {
					log.info(`MCP call name=${fullName} bytes=${limited.originalBytes}`);
				}
				const content = [{ type: "text" as const, text: limited.text }];
				if (result?.isError === true) return { content, details, isError: true };
				return { content, details };
			} catch {
				const reason = timedOut ? "timeout" : controller.signal.aborted ? "aborted" : "error";
				log.warn(`MCP call failed name=${fullName} reason=${reason}`);
				const text =
					reason === "timeout"
						? `MCP tool ${fullName} timed out after ${Math.round(callTimeoutMs / 1000)} s`
						: reason === "aborted"
							? `MCP tool ${fullName} was aborted`
							: `MCP tool ${fullName} failed`;
				return errorResult(text, { ...details, reason });
			} finally {
				clearTimeout(timer);
				signal?.removeEventListener("abort", onAbort);
			}
		},
	};
}

/**
 * Connect the granted servers and build the tool definitions. Never throws: a
 * missing or malformed config gives no MCP tools (ERROR), a server that is down
 * gives none of its tools (WARN), and the built-in tools are unaffected.
 */
export async function connectMcpTools(options: McpBridgeOptions): Promise<McpBridge> {
	const log = options.log;
	const maxBytes = options.maxBytes ?? TOOL_RESULT_MAX_BYTES;
	const callTimeoutMs = options.callTimeoutMs ?? TOOL_CALL_TIMEOUT_SECONDS * 1000;
	const connectTimeoutMs = options.connectTimeoutMs ?? CONNECT_TIMEOUT_MS;
	const discoveryTimeoutMs = options.discoveryTimeoutMs ?? DISCOVERY_TIMEOUT_MS;
	const clientName = options.clientName ?? "fleet-pi-extension";
	const allowed = new Set(options.allowedTools);
	const empty: McpBridge = { tools: [], names: [], close: async () => undefined };

	const servers = readMcpConfig(options.configPath, log);
	if (!servers) return empty;

	const candidates: Array<{ name: string; url: string; headers?: Record<string, string> }> = [];
	for (const [name, config] of Object.entries(servers)) {
		const prefix = toolName(name, "");
		if (!options.allowedTools.some((t) => t.startsWith(prefix))) continue; // nothing granted here
		if (config.type !== "http") {
			log.warn(`MCP server skipped (type is not http) server=${name}`);
			continue;
		}
		if (typeof config.url !== "string" || !URL.canParse(config.url)) {
			log.warn(`MCP server skipped (no valid url) server=${name}`);
			continue;
		}
		candidates.push({ name, url: config.url, headers: stringHeaders(config.headers) });
	}

	const overall = AbortSignal.timeout(discoveryTimeoutMs);
	const settled = await Promise.allSettled(
		candidates.map((c) =>
			connectServer(c.name, c.url, c.headers, AbortSignal.any([AbortSignal.timeout(connectTimeoutMs), overall]), clientName),
		),
	);

	const connected: ConnectedServer[] = [];
	const tools: ToolDefinition[] = [];
	const names: string[] = [];
	settled.forEach((outcome, i) => {
		const name = candidates[i].name;
		if (outcome.status === "rejected") {
			log.warn(`MCP server down server=${name}`);
			return;
		}
		const server = outcome.value;
		connected.push(server);
		for (const tool of server.tools) {
			const fullName = toolName(server.name, tool.name);
			if (!allowed.has(fullName)) continue;
			if (fullName.length > MAX_TOOL_NAME_LENGTH) {
				log.warn(`MCP name over 64 chars skipped name=${fullName} length=${fullName.length}`);
				continue;
			}
			if (!TOOL_NAME_PATTERN.test(fullName)) {
				log.warn(`MCP name has characters outside [A-Za-z0-9_-] skipped server=${server.name}`);
				continue;
			}
			if (names.includes(fullName)) continue;
			tools.push(createToolDefinition(server, tool, fullName, maxBytes, callTimeoutMs, log));
			names.push(fullName);
		}
	});
	log.info(`MCP tools registered count=${names.length}${names.length > 0 ? ` names=${names.join(",")}` : ""}`);

	return {
		tools,
		names,
		close: async () => {
			await Promise.allSettled(connected.map((s) => s.client.close()));
		},
	};
}
