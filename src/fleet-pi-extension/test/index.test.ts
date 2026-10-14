// The extension entry point (phleet #458): every session_shutdown closes the MCP bridge. Pi
// loads the extension again after a fork, so a bridge left open there would leak.

import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, test } from "node:test";
import fleetPiExtension from "../index.ts";
import { AGENT_DIR_ENV, CONFIG_FILE_NAME, SUMMARY_MAX_TOKENS, TOOL_CALL_TIMEOUT_SECONDS, TOOL_RESULT_MAX_BYTES } from "../config.ts";
import { startMcpStub, type McpStub } from "./stubs/mcp.ts";

let dir: string;
let memory: McpStub;
const savedAgentDir = process.env[AGENT_DIR_ENV];

before(async () => {
	dir = mkdtempSync(join(tmpdir(), "fleet-pi-index-"));
	memory = await startMcpStub({ tools: [{ name: "memory_get", text: "remembered" }] });
	const mcpConfigPath = join(dir, "mcp.json");
	writeFileSync(mcpConfigPath, JSON.stringify({ mcpServers: { "fleet-memory": { type: "http", url: memory.url } } }));
	writeFileSync(
		join(dir, CONFIG_FILE_NAME),
		JSON.stringify({
			mcpConfigPath,
			allowedTools: ["mcp__fleet-memory__memory_get"],
			toolResultMaxBytes: TOOL_RESULT_MAX_BYTES,
			toolCallTimeoutSeconds: TOOL_CALL_TIMEOUT_SECONDS,
			summaryMaxTokens: SUMMARY_MAX_TOKENS,
		}),
	);
	process.env[AGENT_DIR_ENV] = dir;
});

after(async () => {
	if (savedAgentDir === undefined) delete process.env[AGENT_DIR_ENV];
	else process.env[AGENT_DIR_ENV] = savedAgentDir;
	await memory.close();
	rmSync(dir, { recursive: true, force: true });
});

/** The parts of Pi's ExtensionAPI the extension uses: registered tools and event handlers. */
async function load() {
	const tools: any[] = [];
	const handlers = new Map<string, Array<(event: any, ctx?: any) => unknown>>();
	const pi: any = {
		registerTool: (tool: any) => tools.push(tool),
		on: (event: string, handler: (event: any, ctx?: any) => unknown) => {
			handlers.set(event, [...(handlers.get(event) ?? []), handler]);
		},
	};
	await fleetPiExtension(pi);
	const emit = async (event: string, payload: any) => {
		for (const handler of handlers.get(event) ?? []) await handler(payload);
	};
	const call = async () => (await tools[0].execute("call-1", {}, undefined, undefined, undefined)) as any;
	return { tools, emit, call };
}

for (const reason of ["fork", "quit"]) {
	test(`session_shutdown (${reason}) closes the MCP bridge`, async () => {
		const extension = await load();
		assert.deepEqual(
			extension.tools.map((t) => t.name),
			["mcp__fleet-memory__memory_get"],
		);
		assert.equal((await extension.call()).isError, undefined, "the tool works before the shutdown");

		await extension.emit("session_shutdown", { type: "session_shutdown", reason });

		const result = await extension.call();
		assert.equal(result.isError, true, "the bridge is closed");
		assert.equal(result.content[0].text, "MCP tool mcp__fleet-memory__memory_get failed");
	});
}
