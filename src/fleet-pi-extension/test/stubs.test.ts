// The stub CLI contract shared with the C# RealPi tests (phleet #386 §15).

import assert from "node:assert/strict";
import { type ChildProcess, spawn } from "node:child_process";
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { after, before, describe, test } from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { INSTRUCTION } from "../compaction.ts";

const STUBS = fileURLToPath(new URL("./stubs/", import.meta.url));

async function spawnStub(script: string, args: string[]): Promise<{ child: ChildProcess; url: string }> {
	const child = spawn(process.execPath, [join(STUBS, script), ...args], { stdio: ["ignore", "pipe", "pipe"] });
	const url = await new Promise<string>((resolve, reject) => {
		let out = "";
		child.stdout!.setEncoding("utf8");
		child.stdout!.on("data", (chunk: string) => {
			out += chunk;
			const nl = out.indexOf("\n");
			if (nl >= 0) {
				const first = out.slice(0, nl);
				const match = /^LISTENING (http:\/\/127\.0\.0\.1:\d+(?:\/mcp)?)$/.exec(first);
				if (match) resolve(match[1]);
				else reject(new Error(`unexpected first line: ${first}`));
			}
		});
		child.once("exit", (code) => reject(new Error(`stub exited ${code}`)));
	});
	return { child, url };
}

async function stop(child: ChildProcess): Promise<void> {
	if (child.exitCode !== null) return;
	const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
	child.kill("SIGTERM");
	await exited;
}

function records(dir: string): any[] {
	return readdirSync(dir)
		.filter((f) => /^\d{4}\.json$/.test(f))
		.sort()
		.map((f) => JSON.parse(readFileSync(join(dir, f), "utf8")));
}

function userMessage(text: string): any {
	return { role: "user", content: [{ type: "text", text }] };
}

async function post(url: string, body: unknown): Promise<Response> {
	return fetch(`${url}/v1/messages?beta=true`, {
		method: "POST",
		headers: { "content-type": "application/json", "x-api-key": "phleet-local-no-auth" },
		body: JSON.stringify(body),
	});
}

async function json(response: Promise<Response>): Promise<any> {
	return (await response).json();
}

function sseEvents(text: string): any[] {
	return text
		.split("\n\n")
		.map((chunk) => chunk.split("\n").find((l) => l.startsWith("data: ")))
		.filter((l): l is string => !!l)
		.map((l) => JSON.parse(l.slice(6)));
}

describe("anthropic stub CLI", () => {
	let dir: string;
	let child: ChildProcess;
	let url: string;

	before(async () => {
		dir = mkdtempSync(join(tmpdir(), "fleet-pi-stub-"));
		const script = {
			main: [
				{ text: "Hello", usage: { input: 50000, cache_read: 1234 } },
				{ tool_use: { name: "bash", input: { command: "ls" } } },
				{ overflow400: { n_prompt: 140000, n_ctx: 131072 } },
				{ status: 500 },
				{ delay_ms: 50, text: "late" },
			],
			summary: [{ max_tokens: "partial" }, { empty: true }],
			default_summary: [{ text: "Scripted default." }],
		};
		writeFileSync(join(dir, "script.json"), JSON.stringify(script));
		({ child, url } = await spawnStub("anthropic.ts", ["--record-dir", join(dir, "rec"), "--script", join(dir, "script.json")]));
	});
	after(async () => {
		await stop(child);
		rmSync(dir, { recursive: true, force: true });
	});

	test("serves the script in order, records every request, answers each kind from its list", async () => {
		const streamed = await post(url, { model: "m", stream: true, max_tokens: 10, messages: [userMessage("hi")] });
		assert.equal(streamed.status, 200);
		assert.match(streamed.headers.get("content-type") ?? "", /text\/event-stream/);
		const events = sseEvents(await streamed.text());
		assert.equal(events[0].type, "message_start");
		assert.equal(events[0].message.usage.input_tokens, 50000);
		assert.equal(events[0].message.usage.cache_read_input_tokens, 1234);
		assert.ok(events.some((e) => e.type === "content_block_delta" && e.delta.text === "Hello"));
		assert.equal(events.find((e) => e.type === "message_delta").delta.stop_reason, "end_turn");
		assert.equal(events[events.length - 1].type, "message_stop");

		const tool = await json(post(url, { model: "m", stream: false, messages: [userMessage("run")] }));
		assert.equal(tool.stop_reason, "tool_use");
		assert.deepEqual(tool.content[0], { type: "tool_use", id: tool.content[0].id, name: "bash", input: { command: "ls" } });

		const overflow = await post(url, { model: "m", messages: [userMessage("big")] });
		assert.equal(overflow.status, 400);
		assert.equal(
			await overflow.text(),
			'{"type":"error","error":{"type":"invalid_request_error","message":"Prompt has 140000 tokens, but the configured context size is 131072 tokens","n_prompt_tokens":140000,"n_ctx":131072}}',
		);
		assert.equal((await post(url, { model: "m", messages: [userMessage("x")] })).status, 500);
		assert.equal((await json(post(url, { model: "m", messages: [userMessage("x")] }))).content[0].text, "late");

		const summary = await json(post(url, { model: "m", messages: [userMessage("a"), { role: "assistant", content: [{ type: "text", text: "b" }] }, userMessage(INSTRUCTION)] }));
		assert.equal(summary.stop_reason, "max_tokens");
		assert.equal(summary.content[0].text, "partial");
		const empty = await json(post(url, { model: "m", messages: [userMessage(INSTRUCTION)] }));
		assert.deepEqual(empty.content, []);
		assert.equal(empty.stop_reason, "end_turn");

		const def = await json(
			post(url, { model: "m", system: [{ type: "text", text: "You are a context summarization assistant. Your task is ..." }], messages: [userMessage("<conversation>")] }),
		);
		assert.equal(def.content[0].text, "Scripted default.");
		const def2 = await json(post(url, { model: "m", system: "You are a context summarization assistant.", messages: [userMessage("x")] }));
		assert.equal(def2.content[0].text, "Default summary.", "exhausted default_summary list");
		const exhausted = await json(post(url, { model: "m", messages: [userMessage("more")] }));
		assert.equal(exhausted.content[0].text, "OK", "exhausted main list");

		const recorded = records(join(dir, "rec"));
		assert.equal(recorded.length, 10);
		assert.deepEqual(Object.keys(recorded[0]).sort(), ["body", "headers"]);
		assert.equal(recorded[0].headers["x-api-key"], "phleet-local-no-auth");
		assert.equal(recorded[0].body.messages[0].content[0].text, "hi");
		assert.equal(readdirSync(join(dir, "rec")).sort()[0], "0001.json");
	});
});

describe("mcp stub CLI", () => {
	let dir: string;
	let child: ChildProcess;
	let url: string;

	before(async () => {
		dir = mkdtempSync(join(tmpdir(), "fleet-pi-mcpstub-"));
		writeFileSync(join(dir, "tools.json"), JSON.stringify([{ name: "memory_get", text: "remembered", delay_ms: 0, error: false }, { name: "memory_bad", text: "nope", error: true }]));
		({ child, url } = await spawnStub("mcp.ts", ["--record-dir", join(dir, "rec"), "--tools", join(dir, "tools.json")]));
	});
	after(async () => {
		await stop(child);
		rmSync(dir, { recursive: true, force: true });
	});

	test("is a Streamable-HTTP MCP server that records the headers of every request", async () => {
		assert.match(url, /\/mcp$/);
		const client = new Client({ name: "contract-test", version: "1.0.0" });
		await client.connect(new StreamableHTTPClientTransport(new URL(url), { requestInit: { headers: { Authorization: "Bearer placeholder" } } }));
		const { tools } = await client.listTools();
		assert.deepEqual(
			tools.map((t) => t.name),
			["memory_get", "memory_bad"],
		);
		const ok: any = await client.callTool({ name: "memory_get", arguments: {} });
		assert.equal(ok.content[0].text, "remembered");
		const bad: any = await client.callTool({ name: "memory_bad", arguments: {} });
		assert.equal(bad.isError, true);
		await client.close();

		const recorded = records(join(dir, "rec"));
		const list = recorded.find((r) => r.body?.method === "tools/list");
		assert.equal(list.headers.authorization, "Bearer placeholder");
		assert.equal(list.httpMethod, "POST");
	});
});
