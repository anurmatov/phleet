// Streamable-HTTP MCP stub for the Fleet Pi extension tests (phleet #386 §15, A17).
//
// CLI (shared with the C# RealPi tests):
//   node test/stubs/mcp.ts --record-dir <dir> --tools <file.json>
// prints `LISTENING http://127.0.0.1:<port>/mcp` as its first stdout line. It is a
// stateless Streamable-HTTP server built on the pinned @modelcontextprotocol/sdk.
// Every HTTP request is recorded as `<dir>/<NNNN>.json` =
// {"httpMethod":"POST","headers":{...},"body":<JSON-RPC body or null>}, NNNN from 0001,
// so a test can check which headers reached `tools/list` and `tools/call`.
//
// Tools file: [{"name":"memory_get","text":"...","delay_ms":0,"error":false}, ...]
// Optional per tool: "description", "inputSchema", "repeat" (the text is repeated
// that many times, for large results).

import { mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type Server as HttpServer, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

export interface StubTool {
	name: string;
	text?: string;
	repeat?: number;
	delay_ms?: number;
	error?: boolean;
	description?: string;
	inputSchema?: Record<string, unknown>;
}

export interface McpRecord {
	httpMethod: string;
	headers: Record<string, string | string[] | undefined>;
	body: any;
}

export interface McpStub {
	url: string;
	port: number;
	records: McpRecord[];
	/** JSON-RPC methods seen, in order (batches flattened). */
	methods(): string[];
	/** Headers of the first request whose JSON-RPC method is `method`. */
	headersFor(method: string): McpRecord["headers"] | undefined;
	close(): Promise<void>;
}

export interface McpStubOptions {
	recordDir?: string;
	tools: StubTool[];
	host?: string;
	port?: number;
}

const DEFAULT_SCHEMA = { type: "object", properties: {} };

function rpcMethods(body: any): string[] {
	const items = Array.isArray(body) ? body : [body];
	return items.flatMap((m) => (m && typeof m.method === "string" ? [m.method] : []));
}

function readBody(req: IncomingMessage): Promise<string> {
	return new Promise((resolve, reject) => {
		const chunks: Buffer[] = [];
		req.on("data", (c: Buffer) => chunks.push(c));
		req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
		req.on("error", reject);
	});
}

function wait(ms: number, signal: AbortSignal): Promise<void> {
	return new Promise((resolve) => {
		if (signal.aborted) return resolve();
		const timer = setTimeout(resolve, ms);
		signal.addEventListener(
			"abort",
			() => {
				clearTimeout(timer);
				resolve();
			},
			{ once: true },
		);
	});
}

function buildServer(tools: StubTool[], closed: AbortSignal): Server {
	const server = new Server({ name: "fleet-mcp-stub", version: "1.0.0" }, { capabilities: { tools: {} } });
	server.setRequestHandler(ListToolsRequestSchema, async () => ({
		tools: tools.map((t) => ({
			name: t.name,
			description: t.description ?? `Stub tool ${t.name}`,
			inputSchema: (t.inputSchema ?? DEFAULT_SCHEMA) as any,
		})),
	}));
	server.setRequestHandler(CallToolRequestSchema, async (request, extra) => {
		const tool = tools.find((t) => t.name === request.params.name);
		if (!tool) {
			return { content: [{ type: "text", text: `unknown tool ${request.params.name}` }], isError: true };
		}
		if (tool.delay_ms && tool.delay_ms > 0) {
			await wait(tool.delay_ms, AbortSignal.any([extra.signal, closed]));
		}
		const text = (tool.text ?? "").repeat(Math.max(1, tool.repeat ?? 1));
		return { content: [{ type: "text", text }], ...(tool.error ? { isError: true } : {}) };
	});
	return server;
}

export async function startMcpStub(options: McpStubOptions): Promise<McpStub> {
	const host = options.host ?? "127.0.0.1";
	if (options.recordDir) mkdirSync(options.recordDir, { recursive: true });
	const records: McpRecord[] = [];
	const shutdown = new AbortController();

	const handle = async (req: IncomingMessage, res: ServerResponse): Promise<void> => {
		const path = new URL(req.url ?? "/", "http://stub").pathname;
		const raw = req.method === "POST" ? await readBody(req) : "";
		let body: any = null;
		if (raw) {
			try {
				body = JSON.parse(raw);
			} catch {
				body = raw;
			}
		}
		const record: McpRecord = { httpMethod: req.method ?? "", headers: { ...req.headers }, body };
		records.push(record);
		if (options.recordDir) {
			writeFileSync(join(options.recordDir, `${String(records.length).padStart(4, "0")}.json`), `${JSON.stringify(record, null, 1)}\n`);
		}
		if (path !== "/mcp") {
			res.writeHead(404).end();
			return;
		}
		if (req.method !== "POST") {
			// Stateless server: no standalone SSE stream and no session to delete.
			res.writeHead(405, { allow: "POST" }).end();
			return;
		}
		const closed = new AbortController();
		res.on("close", () => closed.abort());
		const server = buildServer(options.tools, AbortSignal.any([closed.signal, shutdown.signal]));
		const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined });
		res.on("close", () => {
			void transport.close();
			void server.close();
		});
		await server.connect(transport);
		await transport.handleRequest(req, res, body);
	};

	const http: HttpServer = createServer((req, res) => {
		handle(req, res).catch(() => {
			if (!res.headersSent && !res.destroyed) res.writeHead(500).end();
			else res.destroy();
		});
	});
	await new Promise<void>((resolve, reject) => {
		http.once("error", reject);
		http.listen(options.port ?? 0, host, () => resolve());
	});
	const port = (http.address() as AddressInfo).port;
	return {
		url: `http://${host}:${port}/mcp`,
		port,
		records,
		methods: () => records.flatMap((r) => rpcMethods(r.body)),
		headersFor: (method) => records.find((r) => rpcMethods(r.body).includes(method))?.headers,
		close: () =>
			new Promise<void>((resolve) => {
				shutdown.abort();
				http.closeAllConnections();
				http.close(() => resolve());
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
	const toolsPath = argValue(argv, "--tools");
	if (!recordDir || !toolsPath) {
		process.stderr.write("usage: mcp.ts --record-dir <dir> --tools <file.json> [--port <n>]\n");
		process.exit(2);
	}
	const tools = JSON.parse(readFileSync(toolsPath, "utf8")) as StubTool[];
	const port = argValue(argv, "--port");
	const stub = await startMcpStub({ recordDir, tools, port: port ? Number(port) : undefined });
	process.stdout.write(`LISTENING ${stub.url}\n`);
	const stop = () => {
		void stub.close().then(() => process.exit(0));
	};
	process.on("SIGTERM", stop);
	process.on("SIGINT", stop);
}
