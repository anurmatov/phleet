// A17 against the MCP stub (phleet #386 §4.5, §15: mcp-tools.test.ts).

import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type Server, type Socket } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, describe, test } from "node:test";
import { createMemoryLogger } from "../log.ts";
import { connectMcpTools, limitText, MAX_TOOL_NAME_LENGTH, truncationMarker } from "../mcp-tools.ts";
import { startMcpStub, type McpStub } from "./stubs/mcp.ts";

const HEADER_VALUE = "Bearer placeholder-journal-token";
const SECRET_TEXT = "tool-output-that-must-not-be-logged";
// mcp__fleet-memory__ is 19 characters, so a 46-character tool name gives a 65-character name.
const LONG_TOOL = `memory_${"x".repeat(39)}`;
const EDGE_TOOL = `memory_${"y".repeat(38)}`;

let dir: string;
let memory: McpStub;
let journal: McpStub;
let hanging: Server;
let hangingPort: number;
const sockets = new Set<Socket>();
let closedPort: number;

function writeConfig(name: string, config: unknown): string {
	const path = join(dir, name);
	writeFileSync(path, typeof config === "string" ? config : JSON.stringify(config));
	return path;
}

function text(result: any): string {
	return result.content.map((b: any) => b.text).join("");
}

before(async () => {
	dir = mkdtempSync(join(tmpdir(), "fleet-pi-mcp-"));
	memory = await startMcpStub({
		recordDir: join(dir, "memory-records"),
		tools: [
			{ name: "memory_get", text: `value ${SECRET_TEXT}` },
			{ name: "memory_store", text: "stored" },
			{ name: "memory-search", text: "found" },
			{ name: "memory_slow", text: "late", delay_ms: 10_000 },
			{ name: "memory_big", text: "a".repeat(1024), repeat: 60 },
			{ name: "memory_fail", text: "failed lookup", error: true },
			{ name: LONG_TOOL, text: "long" },
			{ name: EDGE_TOOL, text: "edge" },
		],
	});
	journal = await startMcpStub({ recordDir: join(dir, "journal-records"), tools: [{ name: "search_messages", text: "hits" }] });
	hanging = createServer((socket) => {
		sockets.add(socket); // accept and never answer
		socket.on("close", () => sockets.delete(socket));
	});
	await new Promise<void>((resolve) => hanging.listen(0, "127.0.0.1", () => resolve()));
	hangingPort = (hanging.address() as any).port;
	const probe = createServer();
	await new Promise<void>((resolve) => probe.listen(0, "127.0.0.1", () => resolve()));
	closedPort = (probe.address() as any).port;
	await new Promise<void>((resolve) => probe.close(() => resolve()));
});

after(async () => {
	await memory.close();
	await journal.close();
	for (const s of sockets) s.destroy();
	await new Promise<void>((resolve) => hanging.close(() => resolve()));
	rmSync(dir, { recursive: true, force: true });
});

describe("A17 discovery and grants", () => {
	test("exactly the allowed names are registered, unchanged (hyphens kept), and the header reaches tools/list", async () => {
		const log = createMemoryLogger();
		const configPath = writeConfig("grants.json", {
			mcpServers: {
				"fleet-memory": { type: "http", url: memory.url },
				"fleet-comms-journal": { type: "http", url: journal.url, headers: { Authorization: HEADER_VALUE } },
				"fleet-unused": { type: "http", url: `http://127.0.0.1:${closedPort}/mcp` },
			},
		});
		const allowedTools = [
			"Bash",
			"mcp__fleet-memory__memory_get",
			"mcp__fleet-memory__memory-search",
			"mcp__fleet-memory__memory_not_offered",
			"mcp__fleet-comms-journal__search_messages",
		];
		const bridge = await connectMcpTools({ configPath, allowedTools, log });
		try {
			assert.deepEqual(bridge.names, [
				"mcp__fleet-memory__memory_get",
				"mcp__fleet-memory__memory-search",
				"mcp__fleet-comms-journal__search_messages",
			]);
			assert.deepEqual(
				bridge.tools.map((t) => t.name),
				bridge.names,
			);
			assert.equal(journal.headersFor("tools/list")?.authorization, HEADER_VALUE, "configured header reaches tools/list");
			assert.equal(memory.headersFor("tools/list")?.authorization, undefined, "no header where none is configured");
			// The ungranted server is never contacted, so it raises no "down" Warning.
			assert.ok(!log.lines.some((l) => l.includes("fleet-unused")));

			const result: any = await bridge.tools[2].execute("call-1", { query: "x" }, undefined, undefined, undefined as any);
			assert.equal(text(result), "hits");
			assert.equal(result.isError, undefined);
			const callHeaders = journal.records.filter((r) => JSON.stringify(r.body ?? "").includes('"tools/call"'));
			assert.equal(callHeaders.length, 1);
			assert.equal(callHeaders[0].headers.authorization, HEADER_VALUE, "header reaches tools/call");
			// The list is fixed at discovery: calls never list again.
			assert.equal(journal.methods().filter((m) => m === "tools/list").length, 1);

			const all = log.lines.join("\n");
			assert.ok(!all.includes(HEADER_VALUE), "header values are never logged");
			await bridge.tools[0].execute("call-2", {}, undefined, undefined, undefined as any);
			assert.ok(!log.lines.join("\n").includes(SECRET_TEXT), "tool output is never logged");
			assert.ok(log.lines.some((l) => l.startsWith("INFO MCP call name=mcp__fleet-memory__memory_get bytes=")));
		} finally {
			await bridge.close();
		}
	});

	test("a 65-character name is skipped with a Warning and never shortened; 64 characters is kept", async () => {
		const log = createMemoryLogger();
		const configPath = writeConfig("long.json", { mcpServers: { "fleet-memory": { type: "http", url: memory.url } } });
		const longName = `mcp__fleet-memory__${LONG_TOOL}`;
		const edgeName = `mcp__fleet-memory__${EDGE_TOOL}`;
		assert.equal(longName.length, 65);
		assert.equal(edgeName.length, MAX_TOOL_NAME_LENGTH);
		const bridge = await connectMcpTools({ configPath, allowedTools: [longName, edgeName], log });
		try {
			assert.deepEqual(bridge.names, [edgeName]);
			assert.ok(!bridge.names.some((n) => n !== edgeName), "nothing renamed or hashed");
			assert.ok(log.lines.includes(`WARN MCP name over 64 chars skipped name=${longName} length=65`));
		} finally {
			await bridge.close();
		}
	});

	test("an unreachable or hanging server hides only its own tools, with a Warning", async () => {
		const log = createMemoryLogger();
		const configPath = writeConfig("down.json", {
			mcpServers: {
				"fleet-memory": { type: "http", url: memory.url },
				"fleet-temporal": { type: "http", url: `http://127.0.0.1:${closedPort}/mcp` },
				"fleet-telegram": { type: "http", url: `http://127.0.0.1:${hangingPort}/mcp` },
			},
		});
		const started = Date.now();
		const bridge = await connectMcpTools({
			configPath,
			allowedTools: ["mcp__fleet-memory__memory_get", "mcp__fleet-temporal__notify_cto", "mcp__fleet-telegram__send_message"],
			log,
			connectTimeoutMs: 400, // test injection of the 15 s per-server limit
		});
		try {
			assert.deepEqual(bridge.names, ["mcp__fleet-memory__memory_get"]);
			assert.ok(log.lines.includes("WARN MCP server down server=fleet-temporal"));
			assert.ok(log.lines.includes("WARN MCP server down server=fleet-telegram"));
			assert.ok(Date.now() - started < 5_000, "the per-server limit bounds discovery");
		} finally {
			await bridge.close();
		}
	});

	test("the 20 s total limit (injected) bounds discovery", async () => {
		const log = createMemoryLogger();
		const configPath = writeConfig("total.json", { mcpServers: { "fleet-telegram": { type: "http", url: `http://127.0.0.1:${hangingPort}/mcp` } } });
		const started = Date.now();
		const bridge = await connectMcpTools({
			configPath,
			allowedTools: ["mcp__fleet-telegram__send_message"],
			log,
			connectTimeoutMs: 60_000,
			discoveryTimeoutMs: 300,
		});
		assert.deepEqual(bridge.names, []);
		assert.ok(Date.now() - started < 5_000);
		assert.ok(log.lines.includes("WARN MCP server down server=fleet-telegram"));
	});

	test("a non-http server is skipped with a Warning", async () => {
		const log = createMemoryLogger();
		const configPath = writeConfig("stdio.json", {
			mcpServers: {
				"fleet-local": { command: "node", args: ["server.js"] },
				"fleet-sse": { type: "sse", url: memory.url },
				"fleet-memory": { type: "http", url: memory.url },
			},
		});
		const bridge = await connectMcpTools({
			configPath,
			allowedTools: ["mcp__fleet-local__x", "mcp__fleet-sse__memory_get", "mcp__fleet-memory__memory_get"],
			log,
		});
		try {
			assert.deepEqual(bridge.names, ["mcp__fleet-memory__memory_get"]);
			assert.ok(log.lines.includes("WARN MCP server skipped (type is not http) server=fleet-local"));
			assert.ok(log.lines.includes("WARN MCP server skipped (type is not http) server=fleet-sse"));
		} finally {
			await bridge.close();
		}
	});

	for (const [label, content] of [
		["missing", undefined],
		["malformed JSON", "{not json"],
		["without mcpServers", JSON.stringify({ servers: {} })],
	] as const) {
		test(`${label} config → no MCP tools and an Error line`, async () => {
			const log = createMemoryLogger();
			const configPath = content === undefined ? join(dir, "absent.json") : writeConfig(`bad-${label.length}.json`, content);
			const bridge = await connectMcpTools({ configPath, allowedTools: ["mcp__fleet-memory__memory_get"], log });
			assert.deepEqual(bridge.names, []);
			assert.equal(log.lines.filter((l) => l.startsWith("ERROR ")).length, 1);
		});
	}
});

describe("A17 calls", () => {
	async function bridgeFor(tools: string[], callTimeoutMs?: number) {
		const log = createMemoryLogger();
		const configPath = writeConfig(`calls-${tools.join("-").length}.json`, { mcpServers: { "fleet-memory": { type: "http", url: memory.url } } });
		const bridge = await connectMcpTools({ configPath, allowedTools: tools.map((t) => `mcp__fleet-memory__${t}`), log, callTimeoutMs });
		return { bridge, log, tool: (name: string) => bridge.tools.find((t) => t.name === `mcp__fleet-memory__${name}`)! };
	}

	test("a timeout gives exactly one error result", async () => {
		const { bridge, log, tool } = await bridgeFor(["memory_slow"], 300);
		try {
			const started = Date.now();
			const result: any = await tool("memory_slow").execute("c", {}, undefined, undefined, undefined as any);
			assert.equal(result.isError, true);
			assert.equal(result.content.length, 1);
			assert.match(text(result), /timed out/);
			assert.ok(Date.now() - started < 5_000);
			assert.ok(log.lines.includes("WARN MCP call failed name=mcp__fleet-memory__memory_slow reason=timeout"));
		} finally {
			await bridge.close();
		}
	});

	test("Pi's abort signal cancels the call: exactly one error result", async () => {
		const { bridge, log, tool } = await bridgeFor(["memory_slow"]);
		try {
			const controller = new AbortController();
			setTimeout(() => controller.abort(), 100);
			const result: any = await tool("memory_slow").execute("c", {}, controller.signal, undefined, undefined as any);
			assert.equal(result.isError, true);
			assert.equal(result.content.length, 1);
			assert.match(text(result), /aborted/);
			assert.ok(log.lines.includes("WARN MCP call failed name=mcp__fleet-memory__memory_slow reason=aborted"));

			const pre = new AbortController();
			pre.abort();
			const early: any = await tool("memory_slow").execute("c2", {}, pre.signal, undefined, undefined as any);
			assert.equal(early.isError, true);
		} finally {
			await bridge.close();
		}
	});

	test("a 60 KiB result is cut to 49152 bytes ending with the marker", async () => {
		const { bridge, log, tool } = await bridgeFor(["memory_big"]);
		try {
			const result: any = await tool("memory_big").execute("c", {}, undefined, undefined, undefined as any);
			const out = text(result);
			assert.equal(Buffer.byteLength(out, "utf8"), 49152);
			assert.ok(out.endsWith("[fleet-mcp: output truncated from 61440 to 49152 bytes]"));
			assert.ok(out.startsWith("aaaa"));
			assert.equal(result.isError, undefined);
			assert.ok(log.lines.includes("WARN MCP result truncated name=mcp__fleet-memory__memory_big bytes=61440 limit=49152"));
		} finally {
			await bridge.close();
		}
	});

	test("an MCP isError result is one error result", async () => {
		const { bridge, tool } = await bridgeFor(["memory_fail"]);
		try {
			const result: any = await tool("memory_fail").execute("c", {}, undefined, undefined, undefined as any);
			assert.equal(result.isError, true);
			assert.equal(text(result), "failed lookup");
		} finally {
			await bridge.close();
		}
	});

	test("limitText keeps short text and cuts on a UTF-8 boundary", () => {
		assert.deepEqual(limitText("short", 49152), { text: "short", truncated: false, originalBytes: 5 });
		const cut = limitText("é".repeat(100), 64);
		assert.ok(Buffer.byteLength(cut.text) <= 64);
		assert.ok(cut.text.endsWith(truncationMarker(200, 64)));
		assert.ok(!cut.text.includes("�"));
	});
});
