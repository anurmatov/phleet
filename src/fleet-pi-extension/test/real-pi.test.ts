// Real pinned Pi 1.0.4 in RPC mode with the §4.2 flags, files and this extension,
// against the Anthropic and MCP stubs (phleet #386 A18–A22, A29, A31).
//
// Pi comes from PI_BIN, else this package's pinned devDependency. When neither
// exists the suite is skipped with a message, or fails when PI_REALPI_STRICT=1.
// W = 65536, so the threshold is 49152; the stub reports large `input` usage to
// cross it quickly, and messages are sized against keepRecentTokens = 8192.

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { after, before, describe, test } from "node:test";
import { estimateTokens } from "@earendil-works/pi-coding-agent";
import { INSTRUCTION } from "../compaction.ts";
import {
	BUILT_INS,
	checkPrefixSummary,
	checkToolPairing,
	filler,
	lastText,
	makeDirs,
	PiProcess,
	type PiRecord,
	readSession,
	resolvePi,
	TEST_EXTENSION,
	type TestDirs,
	writeAgentFiles,
} from "./helpers/pi-harness.ts";
import { type AnthropicStub, type StubScript, startAnthropicStub } from "./stubs/anthropic.ts";
import { type McpStub, type StubTool, startMcpStub } from "./stubs/mcp.ts";

const PI = resolvePi();
const STRICT = process.env.PI_REALPI_STRICT === "1";
const SKIP = PI || STRICT ? false : "real Pi not found: set PI_BIN or install the pinned devDependency (PI_REALPI_STRICT=1 makes this a failure)";
const TIMEOUT = { timeout: 180_000 };

const CROSS = { input: 50_000 }; // reported usage that crosses 49152 and still fits the size check
const FALLBACK = /^WARN fast-path fallback reason=/;
const NOT_EXTENDING = /does not extend the last request/;
const PREFIX_INFO = /^INFO pi compaction (threshold|manual) path=prefix tokensBefore=\d+ tail=\d+ splitTurn=(true|false) summary=\d+ cached=\d+ ms=\d+$/;

interface Setup {
	dirs: TestDirs;
	stub: AnthropicStub;
	mcp?: McpStub;
	start(options?: { thinking?: "off" | "medium"; modes?: string; env?: Record<string, string>; session?: string; compactionEnabled?: boolean }): Promise<PiProcess>;
	close(): Promise<void>;
}

async function setup(label: string, script: StubScript, options: { allowedTools?: string[]; mcpTools?: StubTool[]; mcpHeaders?: Record<string, string> } = {}): Promise<Setup> {
	const dirs = makeDirs(label);
	const stub = await startAnthropicStub({ recordDir: dirs.records, script });
	let mcp: McpStub | undefined;
	if (options.mcpTools) {
		mcp = await startMcpStub({ recordDir: dirs.mcpRecords, tools: options.mcpTools });
		writeFileSync(
			dirs.mcpConfig,
			JSON.stringify({ mcpServers: { "fleet-memory": { type: "http", url: mcp.url, ...(options.mcpHeaders ? { headers: options.mcpHeaders } : {}) } } }),
		);
	}
	const granted = options.allowedTools ?? [];
	const processes: PiProcess[] = [];
	return {
		dirs,
		stub,
		mcp,
		async start(o = {}) {
			writeAgentFiles(dirs, { baseUrl: stub.url, allowedTools: granted, compactionEnabled: o.compactionEnabled });
			const pi = await PiProcess.start({
				pi: PI!,
				dirs,
				thinking: o.thinking ?? "medium",
				grantedTools: granted,
				session: o.session,
				preExtensions: o.modes ? [TEST_EXTENSION] : [],
				env: { ...(o.modes ? { FLEET_PI_TEST_EXT_MODE: o.modes } : {}), ...o.env },
			});
			processes.push(pi);
			return pi;
		},
		async close() {
			for (const p of processes) await p.stop();
			await stub.close();
			await mcp?.close();
			dirs.cleanup();
		},
	};
}

function kinds(stub: AnthropicStub): string[] {
	return stub.requests.map((r) => r.kind);
}

/** The main request recorded last before position `index`. */
function mainBefore(stub: AnthropicStub, index: number): any {
	for (let i = index - 1; i >= 0; i--) if (stub.requests[i].kind === "main") return stub.requests[i].body;
	return undefined;
}

function lastAssistant(records: PiRecord[]): any {
	const ends = records.filter((r) => r.type === "message_end" && r.message?.role === "assistant");
	return ends[ends.length - 1]?.message;
}

function fallbackReasons(pi: PiProcess): string[] {
	return pi.warnings().filter((l) => FALLBACK.test(l)).map((l) => l.replace(FALLBACK, ""));
}

describe("real Pi 1.0.4 with the Fleet extension", { skip: SKIP }, () => {
	if (!PI) {
		test("the pinned pi binary is installed (PI_REALPI_STRICT=1)", () => {
			assert.fail("pi not found: set PI_BIN or run npm ci in src/fleet-pi-extension");
		});
		return;
	}

	test("pi --version is the pin", () => {
		const out = spawnSync(PI.command, [...PI.args, "--version"], { encoding: "utf8", env: { PATH: process.env.PATH ?? "", PI_OFFLINE: "1" } });
		assert.equal(out.stdout.trim(), "1.0.4", `pi from ${PI.source}`);
	});

	describe("A18 post-run and mid-run prefix compaction, A21, A22, A29 split turn, A31 tools", () => {
		let s: Setup;
		let pi: PiProcess;
		let sessionFile: string;
		const granted = ["mcp__fleet-memory__memory_get"];

		before(async () => {
			s = await setup(
				"a18",
				{
					main: [
						{ tool_use: { name: "mcp__fleet-memory__memory_get", input: {} } },
						{ text: "OK" },
						{ text: "Noted.", usage: CROSS },
						{ tool_use: { name: "bash", input: { command: "cat big.txt" } }, usage: CROSS },
						{ text: "Printed." },
					],
					summary: [{ text: "Goal: keep anchor alpha-bravo.\nNext steps: continue." }, { text: "Goal: second summary keeps alpha-bravo." }],
				},
				{ allowedTools: granted, mcpTools: [{ name: "memory_get", text: "memory value 42" }], mcpHeaders: { "X-Fleet-Test": "header-placeholder" } },
			);
			writeFileSync(join(s.dirs.cwd, "big.txt"), filler(8750, "file"));
			pi = await s.start({ thinking: "medium" });
			sessionFile = (await pi.state()).sessionFile;
			await pi.prompt("Plant anchor alpha-bravo, then read the memory.");
			await pi.prompt(`${filler(8600, "turn2")}\nReply Noted.`);
			await pi.prompt("Print big.txt with bash.");
		}, TIMEOUT);
		after(() => s?.close());

		test("A31: the first request declares exactly read,bash,edit,write plus the granted names", () => {
			const first = s.stub.requests[0].body;
			assert.deepEqual(
				first.tools.map((t: any) => t.name),
				[...BUILT_INS, ...granted],
			);
			assert.ok(!first.tools.some((t: any) => /codemode|tool_search|_resource/.test(t.name)));
		});

		test("A17 in real Pi: the MCP call goes through the bridge with the configured header", () => {
			assert.equal(s.mcp!.headersFor("tools/list")?.["x-fleet-test"], "header-placeholder");
			assert.ok(s.mcp!.methods().includes("tools/call"));
			assert.ok(JSON.stringify(s.stub.requests[1].body.messages).includes("memory value 42"));
		});

		test("A18: exactly one prefix-shaped summary request per compaction, nothing else", () => {
			const ends = pi.events("compaction_end");
			assert.equal(ends.length, 2);
			assert.equal(kinds(s.stub).filter((k) => k === "summary").length, 2, "one summary request per compaction");
			assert.equal(kinds(s.stub).filter((k) => k === "default_summary").length, 0, "Pi's default summary never ran");
			s.stub.requests.forEach((r, i) => {
				if (r.kind !== "summary") return;
				assert.deepEqual(checkPrefixSummary(r.body, mainBefore(s.stub, i)), [], `summary request ${i + 1}`);
				const last = r.body.messages[r.body.messages.length - 1];
				assert.equal(lastText(last), INSTRUCTION);
			});
		});

		test("A18: compaction_end carries details.path prefix, tailTokens and isSplitTurn (A29: mid-run split)", () => {
			const ends = pi.events("compaction_end");
			assert.deepEqual(
				ends.map((e) => [e.reason, e.willRetry, e.result?.details?.path, e.result?.details?.isSplitTurn, typeof e.result?.details?.tailTokens]),
				[
					["threshold", false, "prefix", false, "number"],
					["threshold", false, "prefix", true, "number"],
				],
			);
			assert.ok(ends.every((e) => e.result.details.tailTokens > 0 && e.result.details.tailTokens <= 10240));
		});

		test("A18: the INFO line is logged and there is no fallback or 'does not extend' Warning", () => {
			const infos = pi.infos().filter((l) => PREFIX_INFO.test(l));
			assert.equal(infos.length, 2, pi.stderr.join("\n"));
			assert.match(infos[0], /splitTurn=false/);
			assert.match(infos[1], /splitTurn=true/);
			assert.deepEqual(pi.warnings().filter((l) => FALLBACK.test(l) || NOT_EXTENDING.test(l)), []);
		});

		test("A22: no role:system in messages, and one identical system text across main requests", () => {
			const mains = s.stub.requests.filter((r) => r.kind === "main").map((r) => r.body);
			assert.ok(mains.length >= 5);
			for (const body of mains) assert.ok(!body.messages.some((m: any) => m.role === "system"));
			assert.equal(new Set(mains.map((b) => JSON.stringify(b.system))).size, 1);
		});

		test("A21: the first kept entry is not a toolResult and every kept toolResult keeps its toolCall", () => {
			assert.deepEqual(checkToolPairing(readSession(sessionFile)), []);
		});

		test("the turn after the mid-run compaction completes", () => {
			assert.equal(lastText(lastAssistant(pi.records)), "Printed.");
		});
	});

	describe("A18 negative control and A31 handled prompt (test-only extension)", () => {
		let s: Setup;
		let pi: PiProcess;
		let handled: { disposition: string };
		let requestsAfterHandled = -1;

		before(async () => {
			s = await setup("a18neg", { main: [{ text: "OK" }, { text: "Noted.", usage: CROSS }], summary: [{ text: "Goal: summary despite the change." }] });
			pi = await s.start({ modes: "mutate_message,noop_command" });
			handled = await pi.prompt("/fleet-test-noop");
			await new Promise((r) => setTimeout(r, 500));
			requestsAfterHandled = s.stub.requests.length;
			await pi.prompt("hello");
			await pi.prompt(`${filler(8600, "neg")}\nReply Noted.`);
		}, TIMEOUT);
		after(() => s?.close());

		test("A31: a prompt Pi handles as a command is acknowledged `handled` and reaches no model", () => {
			assert.equal(handled.disposition, "handled");
			assert.equal(requestsAfterHandled, 0);
		});

		test("a changed earlier message gives the 'does not extend' Warning with its index, and the request is still sent", () => {
			assert.ok(pi.warnings().includes("WARN summary request does not extend the last request (first difference at message 1)"), pi.stderr.join("\n"));
			assert.equal(kinds(s.stub).filter((k) => k === "summary").length, 1);
			const end = pi.events("compaction_end")[0];
			assert.equal(end.result?.details?.path, "prefix");
		});
	});

	describe("A19 step-5 refusals and size in one session", () => {
		let s: Setup;
		let pi: PiProcess;
		const PARTIAL = "Goal: PARTIAL-SUMMARY-NEVER-USED";

		before(async () => {
			s = await setup("a19", {
				main: [
					{ text: "OK" },
					{ text: "n2", usage: CROSS },
					{ text: "n3", usage: CROSS },
					{ text: "n4", usage: CROSS },
					{ text: "n5", usage: CROSS },
					{ text: "n6", usage: CROSS },
					{ text: "n7", usage: { input: 63_000 } },
				],
				summary: [
					{ tool_use: { name: "bash", input: { command: "true" } } },
					{ max_tokens: PARTIAL },
					{ status: 500 },
					{ empty: true },
					{ delay_ms: 5_000, text: "late" },
				],
			});
			pi = await s.start({ thinking: "off", env: { FLEET_PI_SUMMARY_TIMEOUT_MS: "1500" } });
			await pi.prompt("hello");
			for (let turn = 2; turn <= 7; turn++) await pi.prompt(`${filler(8600, `t${turn}`)}\nReply briefly.`);
		}, TIMEOUT);
		after(() => s?.close());

		test("each refusal falls back with its literal: toolUse, length, error, empty, timeout, size", () => {
			assert.deepEqual(fallbackReasons(pi), ["toolUse", "length", "error", "empty", "timeout", "size"], pi.stderr.join("\n"));
		});

		test("after each refused summary the stub sees Pi's default summary prompt; size sends no summary request", () => {
			const k = kinds(s.stub);
			const summaries = k.flatMap((kind, i) => (kind === "summary" ? [i] : []));
			assert.equal(summaries.length, 5);
			for (const i of summaries) assert.equal(k[i + 1], "default_summary", `request ${i + 2}`);
			assert.equal(k.filter((x) => x === "default_summary").length, 6);
			assert.equal(k[k.length - 1], "default_summary", "the size fallback goes straight to the default summary");
			for (const i of summaries) assert.deepEqual(checkPrefixSummary(s.stub.requests[i].body, mainBefore(s.stub, i)), []);
		});

		test("a max_tokens stop with text (length) is never used as the summary", () => {
			const ends = pi.events("compaction_end");
			assert.equal(ends.length, 6);
			for (const e of ends) {
				assert.ok(e.result, "Pi's default compaction completed");
				assert.notEqual(e.result.details?.path, "prefix");
				assert.ok(!String(e.result.summary).includes("PARTIAL-SUMMARY-NEVER-USED"));
			}
		});
	});

	describe("A19 no_capture after a --session restart", () => {
		let s: Setup;
		let pi2: PiProcess;
		let firstOfRestart = -1;

		before(async () => {
			s = await setup("nocap", { main: [{ text: "OK" }, { text: "Noted.", usage: CROSS }, { text: "Continued." }] });
			// First process: compaction off, so the session ends above the threshold without compacting.
			const pi1 = await s.start({ compactionEnabled: false });
			await pi1.prompt("hello");
			await pi1.prompt(`${filler(8600, "nocap")}\nReply Noted.`);
			const sessionFile = (await pi1.state()).sessionFile;
			await pi1.stop();
			firstOfRestart = s.stub.requests.length;
			pi2 = await s.start({ session: sessionFile });
			await pi2.prompt("continue");
		}, TIMEOUT);
		after(() => s?.close());

		test("the first compaction of the restarted process takes Pi's default path with reason=no_capture", () => {
			assert.deepEqual(fallbackReasons(pi2), ["no_capture"]);
			assert.equal(s.stub.requests[firstOfRestart].kind, "default_summary");
			assert.ok(!kinds(s.stub).includes("summary"));
			assert.equal(lastText(lastAssistant(pi2.records)), "Continued.");
		});
	});

	for (const mode of ["usage_unavailable", "kept_unresolved"] as const) {
		describe(`A19 ${mode} (test-only extension)`, () => {
			let s: Setup;
			let pi: PiProcess;

			before(async () => {
				s = await setup(mode, { main: [{ text: "OK" }, { text: "Noted.", usage: CROSS }] });
				pi = await s.start({ modes: mode });
				await pi.prompt("hello");
				await pi.prompt(`${filler(8600, mode)}\nReply Noted.`);
			}, TIMEOUT);
			after(() => s?.close());

			test(`falls back with reason=${mode}, sends no summary request and estimates no tail`, () => {
				assert.deepEqual(fallbackReasons(pi), [mode]);
				assert.ok(!kinds(s.stub).includes("summary"));
				assert.ok(kinds(s.stub).includes("default_summary"));
				assert.ok(!pi.warnings().some((l) => l.includes("kept tail")));
				assert.ok(pi.events("compaction_end")[0]?.result);
			});
		});
	}

	describe("A20 overflow recovery (and A19 overflow, A21 after overflow)", () => {
		let s: Setup;
		let pi: PiProcess;
		let sessionFile: string;
		const overflow = { overflow400: { n_prompt: 140_000, n_ctx: 131_072 } };
		let once: PiRecord[] = [];
		let twice: PiRecord[] = [];
		let next: PiRecord[] = [];
		let pairingAfterOnce: string[] = [];

		before(async () => {
			s = await setup("a20", {
				main: [{ text: "OK" }, { text: "Noted." }, overflow, { text: "Recovered after overflow." }, { text: "Noted again." }, overflow, overflow, { text: "Fine." }],
			});
			pi = await s.start({});
			sessionFile = (await pi.state()).sessionFile;
			await pi.prompt("hello");
			await pi.prompt(`${filler(8600, "o2")}\nReply Noted.`);
			once = (await pi.prompt("Go on.")).records;
			pairingAfterOnce = checkToolPairing(readSession(sessionFile));
			await pi.prompt(`${filler(8600, "o4")}\nReply Noted again.`);
			twice = (await pi.prompt("Go on again.")).records;
			next = (await pi.prompt("Are you there?")).records;
		}, TIMEOUT);
		after(() => s?.close());

		test("one 400: compaction_end{reason:overflow, willRetry:true}, Pi's default summary, a successful retry", () => {
			const ends = once.filter((r) => r.type === "compaction_end");
			assert.equal(ends.length, 1);
			assert.equal(ends[0].reason, "overflow");
			assert.equal(ends[0].willRetry, true);
			assert.ok(ends[0].result);
			assert.equal(lastText(lastAssistant(once)), "Recovered after overflow.");
			assert.equal(fallbackReasons(pi)[0], "overflow");
			assert.deepEqual(pairingAfterOnce, []);
		});

		test("two 400s: the recovery fails with an error, and the next prompt succeeds without a reset", (t) => {
			const ends = twice.filter((r) => r.type === "compaction_end");
			assert.equal(ends[0]?.reason, "overflow");
			assert.equal(ends[0]?.willRetry, true, "one compact-and-retry");
			assert.ok(ends[0]?.result);
			// Pi 1.0.4 race: when the failed retry's timestamp equals the compaction entry's (same
			// millisecond), _checkCompaction treats it as pre-compaction and emits no second
			// compaction_end. Either way the run ends in the overflow error, never a second retry.
			assert.ok(ends.length === 1 || ends.length === 2, `compaction_end count ${ends.length}`);
			if (ends.length === 2) {
				assert.equal(ends[1].reason, "overflow");
				assert.equal(ends[1].willRetry, false);
				assert.equal(ends[1].result, undefined);
				assert.match(String(ends[1].errorMessage), /overflow recovery failed/i);
			} else {
				t.diagnostic("Pi emitted no recovery-failed compaction_end (same-millisecond timestamp race)");
			}
			const failed = lastAssistant(twice);
			assert.equal(failed?.stopReason, "error");
			assert.match(String(failed?.errorMessage), /Prompt has 140000 tokens, but the configured context size is 131072 tokens/);
			assert.equal(lastText(lastAssistant(next)), "Fine.");
			assert.equal(lastAssistant(next)?.stopReason, "stop");
			// Before acking the next prompt Pi may re-check the failed message and emit one more
			// "recovery failed" compaction_end (no compaction_start, no result); nothing is compacted or reset.
			assert.equal(next.filter((r) => r.type === "compaction_start").length, 0);
			assert.ok(next.filter((r) => r.type === "compaction_end").every((r) => r.result === undefined && r.willRetry === false));
			assert.ok(fallbackReasons(pi).every((r) => r === "overflow"));
		});
	});

	describe("manual compaction (RPC compact) and a missing .mcp.json", () => {
		let s: Setup;
		let pi: PiProcess;
		let compacted: PiRecord;

		before(async () => {
			s = await setup("manual", { main: [{ text: "OK" }, { text: "Noted." }], summary: [{ text: "Goal: manual summary." }] }, { allowedTools: ["mcp__fleet-memory__memory_get"] });
			rmSync(s.dirs.mcpConfig);
			pi = await s.start({});
			await pi.prompt("hello");
			await pi.prompt(`${filler(8600, "manual")}\nReply Noted.`);
			compacted = await pi.request({ type: "compact" }, 60_000);
		}, TIMEOUT);
		after(() => s?.close());

		test("reason manual takes the fast path", () => {
			assert.equal(compacted.success, true);
			const end = pi.events("compaction_end")[0];
			assert.equal(end?.reason, "manual");
			assert.equal(end?.result?.details?.path, "prefix");
			assert.equal(kinds(s.stub).filter((k) => k === "summary").length, 1);
			assert.ok(pi.infos().some((l) => /^INFO pi compaction manual path=prefix /.test(l)));
		});

		test("a missing MCP config gives one ERROR line, no MCP tools, and the built-ins still work", () => {
			assert.equal(pi.stderr.filter((l) => l.startsWith("ERROR ")).length, 1);
			assert.deepEqual(
				s.stub.requests[0].body.tools.map((t: any) => t.name),
				BUILT_INS,
			);
			assert.equal(lastText(lastAssistant(pi.records)), "Noted.");
		});
	});

	test("an invalid fleet-pi.json fails closed: Pi exits 1 before answering get_state", TIMEOUT, async () => {
		const dirs = makeDirs("badcfg");
		try {
			writeAgentFiles(dirs, { baseUrl: "http://127.0.0.1:9", allowedTools: [] });
			const path = join(dirs.agentDir, "fleet-pi.json");
			writeFileSync(path, JSON.stringify({ ...JSON.parse(readFileSync(path, "utf8")), summaryMaxTokens: 2048 }));
			await assert.rejects(
				PiProcess.start({ pi: PI, dirs, thinking: "off", grantedTools: [] }),
				(e: Error) => /Pi exited \(1\)/.test(e.message) && /ERROR fleet-pi-extension not loaded: fleet-pi\.json: summaryMaxTokens must be 1280/.test(e.message),
			);
		} finally {
			dirs.cleanup();
		}
	});

	describe("A29 kept tail over 10240", () => {
		let s: Setup;
		let pi: PiProcess;
		const reply = "y".repeat(6000);

		before(async () => {
			s = await setup("a29", { main: [{ text: "OK" }, { text: reply, usage: CROSS }], summary: [{ text: "Goal: tail test." }] });
			pi = await s.start({});
			await pi.prompt("hello");
			await pi.prompt(`${filler(9000, "tail")}\nReply with many y.`);
		}, TIMEOUT);
		after(() => s?.close());

		test("a 9000-token last user message gives tailTokens > 10240, the Warning, and still one prefix summary", () => {
			const end = pi.events("compaction_end")[0];
			const tail = end?.result?.details?.tailTokens;
			assert.equal(end?.result?.details?.path, "prefix");
			assert.ok(tail > 10240, `tailTokens ${tail}`);
			assert.ok(pi.warnings().includes(`WARN kept tail over 10240 tail=${tail}`), pi.stderr.join("\n"));
			assert.equal(kinds(s.stub).filter((k) => k === "summary").length, 1);
			assert.ok(estimateTokens({ role: "user", content: filler(9000, "tail"), timestamp: 0 } as any) >= 9000);
		});
	});
});
