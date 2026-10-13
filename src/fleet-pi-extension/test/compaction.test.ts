// Unit tests for the §4.4 fast path (phleet #386 §15: compaction.test.ts).
// Pi's real convertToLlm/estimateTokens are used; the session, model registry and
// provider payload are fakes shaped like Pi 1.0.4's.

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { describe, test } from "node:test";
import { convertToLlm, estimateTokens } from "@earendil-works/pi-coding-agent";
import {
	deepEqual,
	firstDifference,
	INSTRUCTION,
	INSTRUCTION_SHA256,
	registerCompaction,
	S,
	SLOW_COMPACTION_MS,
	stripCacheControl,
	TAIL_BUDGET,
} from "../compaction.ts";
import { createMemoryLogger } from "../log.ts";

const W = 65536;
const SYSTEM = [{ type: "text", text: "You are the Fleet test agent.", cache_control: { type: "ephemeral" } }];
const TOOLS = [
	{ name: "read", description: "Read a file", input_schema: { type: "object", properties: {} } },
	{ name: "mcp__fleet-memory__memory_get", description: "Get", input_schema: { type: "object", properties: {} } },
];

const usage0 = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };

function user(text: string, ts: number): any {
	return { role: "user", content: text, timestamp: ts };
}
function assistant(text: string, ts: number): any {
	return { role: "assistant", content: [{ type: "text", text }], api: "anthropic-messages", provider: "fleet-local", model: "m", usage: usage0, stopReason: "stop", timestamp: ts };
}

/** Anthropic `messages` the way the provider serializes Pi's LLM messages (simplified, deterministic). */
function wire(messages: any[]): any[] {
	return convertToLlm(messages)
		.filter((m: any) => m.role !== "system")
		.map((m: any) =>
			m.role === "user"
				? { role: "user", content: typeof m.content === "string" ? [{ type: "text", text: m.content }] : m.content.map((b: any) => ({ type: "text", text: b.text })) }
				: { role: "assistant", content: m.content.map((b: any) => ({ type: "text", text: b.text })) },
		);
}

function withCacheControl(messages: any[]): any[] {
	const copy = structuredClone(messages);
	const last = copy[copy.length - 1];
	if (last) last.content[last.content.length - 1].cache_control = { type: "ephemeral" };
	return copy;
}

function mainPayload(messages: any[]): any {
	return {
		model: "m",
		messages: withCacheControl(wire(messages)),
		max_tokens: 16384,
		stream: true,
		system: structuredClone(SYSTEM),
		tools: structuredClone(TOOLS),
		thinking: { type: "enabled", budget_tokens: 8192 },
	};
}

interface Reply {
	content?: any[];
	stopReason?: string;
	usage?: any;
	/** Wait for the abort signal instead of answering. */
	hang?: boolean;
	throws?: boolean;
}

function makeRun(options: { messages: any[]; reply?: Reply; usage?: any; keptIndex?: number; now?: () => number; timeoutMs?: number }) {
	const log = createMemoryLogger();
	const handlers = new Map<string, Array<(e: any, c?: any) => any>>();
	const host = {
		on(event: string, handler: (e: any, c?: any) => any) {
			handlers.set(event, [...(handlers.get(event) ?? []), handler]);
			return () => undefined;
		},
	};
	registerCompaction(host as any, { convertToLlm, estimateTokens, log, now: options.now, timeoutMs: options.timeoutMs });
	const emit = async (event: string, payload: any, ctx?: any) => {
		let result: any;
		for (const h of handlers.get(event) ?? []) {
			const r = await h(payload, ctx);
			if (r !== undefined) result = r;
		}
		return result;
	};

	const entries = options.messages.map((m, i) => ({ sourceEntry: { id: `e${i}`, type: "message" }, messages: [m] }));
	const sent: any[] = [];
	const calls: any[] = [];
	const ctx = {
		model: { id: "m", provider: "fleet-local", api: "anthropic-messages" },
		getContextUsage: () => options.usage ?? { tokens: 30000, contextWindow: W, percent: 45 },
		sessionManager: { buildSessionProjection: () => ({ entries, messages: options.messages }) },
		modelRegistry: {
			complete: async (model: any, context: any, opts: any) => {
				calls.push({ model, context, opts });
				const providerPayload = {
					model: "m",
					messages: withCacheControl(wire(context.messages)),
					max_tokens: opts.maxTokens,
					stream: true,
					system: [{ type: "text", text: "NOT THE CAPTURED SYSTEM" }],
					tools: [{ name: "not_captured" }],
				};
				sent.push(await opts.onPayload(providerPayload, model));
				const reply = options.reply ?? {};
				if (reply.throws) throw new Error("boom");
				if (reply.hang) {
					await new Promise<void>((resolve) => opts.signal.addEventListener("abort", () => resolve(), { once: true }));
					return { role: "assistant", content: [], stopReason: "aborted", usage: usage0 };
				}
				return {
					role: "assistant",
					content: reply.content ?? [{ type: "text", text: "Goal: test" }],
					stopReason: reply.stopReason ?? "stop",
					usage: reply.usage ?? { ...usage0, input: 120, cacheRead: 30000, output: 400 },
				};
			},
		},
	};
	const keptIndex = options.keptIndex ?? Math.max(0, options.messages.length - 2);
	const compact = (overrides: Partial<{ reason: string; firstKeptEntryId: string; isSplitTurn: boolean; signal: AbortSignal }> = {}) =>
		emit(
			"session_before_compact",
			{
				type: "session_before_compact",
				reason: overrides.reason ?? "threshold",
				willRetry: false,
				signal: overrides.signal ?? new AbortController().signal,
				branchEntries: [],
				preparation: {
					firstKeptEntryId: overrides.firstKeptEntryId ?? `e${keptIndex}`,
					messagesToSummarize: [],
					turnPrefixMessages: [],
					isSplitTurn: overrides.isSplitTurn ?? false,
					tokensBefore: 50123,
					fileOps: {},
					settings: { enabled: true, reserveTokens: 16384, keepRecentTokens: 8192 },
				},
			},
			ctx,
		);
	const capture = (payload: any) => emit("before_provider_request", { type: "before_provider_request", payload }, ctx);
	const warnings = () => log.lines.filter((l) => l.startsWith("WARN "));
	return { log, emit, ctx, sent, calls, compact, capture, warnings };
}

const conversation = () => [user("plant anchor X", 1), assistant("OK", 2), user("next", 3), assistant("Done", 4)];

describe("instruction (§4.4 step 4)", () => {
	test("is the tested 525-byte ASCII text, byte for byte", () => {
		const bytes = Buffer.from(INSTRUCTION, "utf8");
		assert.equal(bytes.length, 525);
		assert.equal(createHash("sha256").update(bytes).digest("hex"), "b755fed8758856edf45a96afd398a23a4599fe690cd8aecb5a88ea736fc9e4be");
		assert.equal(INSTRUCTION_SHA256, "b755fed8758856edf45a96afd398a23a4599fe690cd8aecb5a88ea736fc9e4be");
		assert.ok([...bytes].every((b) => b >= 0x20 && b < 0x7f), "printable ASCII only, no newline");
		assert.ok(INSTRUCTION.startsWith("Do not call tools.") && INSTRUCTION.endsWith("at most 300 words."));
	});

	test("is one string constant in compaction.ts", () => {
		const source = readFileSync(new URL("../compaction.ts", import.meta.url), "utf8");
		assert.ok(source.includes(JSON.stringify(INSTRUCTION)), "the instruction must be a single string literal");
	});

	test("S is 1280", () => assert.equal(S, 1280));
});

describe("capture", () => {
	test("comes only from before_provider_request, as a deep copy, leaving the payload unchanged", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs });
		const payload = mainPayload(msgs.slice(0, 3));
		const ret = await run.capture(payload);
		assert.equal(ret, undefined, "the capture handler must not replace the payload");
		payload.system[0].text = "mutated after capture";
		const result = await run.compact();
		assert.ok(result?.compaction);
		assert.deepEqual(run.sent[0].system, SYSTEM, "system comes from the copy taken at capture time");
		// The summary call never overwrites the capture: a second compaction still sends the captured system/tools.
		await run.compact();
		assert.deepEqual(run.sent[1].system, SYSTEM);
		assert.deepEqual(run.sent[1].tools, TOOLS);
	});
});

describe("eligibility (§4.4 step 1)", () => {
	test("overflow → undefined, reason=overflow, no request", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		assert.equal(await run.compact({ reason: "overflow" }), undefined);
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=overflow"]);
		assert.equal(run.calls.length, 0);
	});

	test("no capture → reason=no_capture", async () => {
		const run = makeRun({ messages: conversation() });
		assert.equal(await run.compact(), undefined);
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=no_capture"]);
		assert.equal(run.calls.length, 0);
	});

	for (const [label, usage] of [
		["getContextUsage() undefined", undefined],
		["tokens null", { tokens: null, contextWindow: W, percent: null }],
	] as const) {
		test(`${label} → reason=usage_unavailable, nothing estimated`, async () => {
			const msgs = conversation();
			const run = makeRun({ messages: msgs });
			(run.ctx as any).getContextUsage = () => usage;
			await run.capture(mainPayload(msgs.slice(0, 3)));
			assert.equal(await run.compact(), undefined);
			assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=usage_unavailable"]);
			assert.equal(run.calls.length, 0);
		});
	}

	test("size: tokens + 600 + S must be ≤ W − 1024", async () => {
		const msgs = conversation();
		const limit = W - 1024 - 600 - S; // 62632
		const fits = makeRun({ messages: msgs, usage: { tokens: limit, contextWindow: W, percent: 95 } });
		await fits.capture(mainPayload(msgs.slice(0, 3)));
		assert.ok((await fits.compact())?.compaction, "exactly at the limit is eligible");
		const over = makeRun({ messages: msgs, usage: { tokens: limit + 1, contextWindow: W, percent: 95 } });
		await over.capture(mainPayload(msgs.slice(0, 3)));
		assert.equal(await over.compact(), undefined);
		assert.deepEqual(over.warnings(), ["WARN fast-path fallback reason=size"]);
		assert.equal(over.calls.length, 0);
	});

	test("firstKeptEntryId not in the projection → reason=kept_unresolved, no tail estimate, no request", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		assert.equal(await run.compact({ firstKeptEntryId: "not-in-projection" }), undefined);
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=kept_unresolved"]);
		assert.equal(run.calls.length, 0);
	});
});

describe("response refusals (§4.4 step 5)", () => {
	const cases: Array<[string, Reply, string]> = [
		["a toolCall block", { content: [{ type: "text", text: "x" }, { type: "toolCall", id: "t", name: "bash", arguments: {} }], stopReason: "stop" }, "toolUse"],
		["stopReason toolUse", { content: [{ type: "text", text: "x" }], stopReason: "toolUse" }, "toolUse"],
		["length with non-empty text", { content: [{ type: "text", text: "Goal: half a summ" }], stopReason: "length" }, "length"],
		["error", { content: [], stopReason: "error" }, "error"],
		["aborted", { content: [], stopReason: "aborted" }, "aborted"],
		["blank text", { content: [{ type: "text", text: "  \n " }], stopReason: "stop" }, "empty"],
		["no content", { content: [], stopReason: "stop" }, "empty"],
		["another stopReason", { content: [{ type: "text", text: "x" }], stopReason: "deferred" }, "deferred"],
		["complete() throwing", { throws: true }, "error"],
	];
	for (const [label, reply, reason] of cases) {
		test(`${label} → undefined, reason=${reason}`, async () => {
			const msgs = conversation();
			const run = makeRun({ messages: msgs, reply });
			await run.capture(mainPayload(msgs.slice(0, 3)));
			assert.equal(await run.compact(), undefined, "a refused summary is never used");
			assert.equal(run.calls.length, 1);
			assert.deepEqual(run.warnings(), [`WARN fast-path fallback reason=${reason}`]);
		});
	}

	test("an unexpected throw inside the hook → undefined, reason=error", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs });
		(run.ctx as any).sessionManager = {
			buildSessionProjection: () => {
				throw new Error("projection failed");
			},
		};
		await run.capture(mainPayload(msgs.slice(0, 3)));
		assert.equal(await run.compact(), undefined);
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=error"]);
		assert.equal(run.calls.length, 0);
	});

	test("90 s timeout (injected) → reason=timeout", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs, reply: { hang: true }, timeoutMs: 30 });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		assert.equal(await run.compact(), undefined);
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=timeout"]);
	});

	test("Pi aborting the compaction → reason=aborted", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs, reply: { hang: true } });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		const outer = new AbortController();
		const pending = run.compact({ signal: outer.signal });
		setTimeout(() => outer.abort(), 10);
		assert.equal(await pending, undefined);
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=aborted"]);
	});
});

describe("request (§4.4 step 2)", () => {
	test("system/tools from the capture, thinking disabled, max_tokens 1280, ends with the instruction", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs });
		const captured = mainPayload(msgs.slice(0, 3));
		await run.capture(captured);
		await run.compact();
		assert.equal(run.calls.length, 1);
		assert.equal(run.calls[0].opts.maxTokens, 1280);
		const sent = run.sent[0];
		assert.equal(JSON.stringify(sent.system), JSON.stringify(captured.system));
		assert.equal(JSON.stringify(sent.tools), JSON.stringify(captured.tools));
		assert.deepEqual(sent.thinking, { type: "disabled" });
		assert.equal(sent.max_tokens, 1280);
		const last = sent.messages[sent.messages.length - 1];
		assert.equal(last.role, "user");
		assert.equal(last.content[last.content.length - 1].text, INSTRUCTION);
		assert.equal(firstDifference(captured.messages, sent.messages), -1, "the summary request extends the captured one");
		assert.deepEqual(run.warnings(), []);
	});

	test("prefix guard: a changed earlier message logs its index and the request is still sent", async () => {
		const msgs = conversation();
		const run = makeRun({ messages: msgs });
		const captured = mainPayload(msgs.slice(0, 3));
		captured.messages[1].content[0].text = "a different reply";
		await run.capture(captured);
		const result = await run.compact();
		assert.deepEqual(run.warnings(), ["WARN summary request does not extend the last request (first difference at message 1)"]);
		assert.equal(run.sent.length, 1, "still sent");
		assert.ok(result?.compaction, "and still accepted");
	});

	test("prefix guard ignores cache_control and key order", () => {
		const a = [{ role: "user", content: [{ type: "text", text: "x", cache_control: { type: "ephemeral" } }] }];
		const b = [{ content: [{ text: "x", type: "text" }], role: "user" }, { role: "user", content: [] }];
		assert.equal(firstDifference(a, b), -1);
		assert.equal(firstDifference(a, []), 0);
		assert.equal(firstDifference([...a, a[0]], b), 1);
		assert.deepEqual(stripCacheControl({ a: [{ cache_control: 1, b: 2 }] }), { a: [{ b: 2 }] });
		assert.ok(deepEqual({ a: 1, b: undefined }, { a: 1 }));
		assert.ok(!deepEqual([1], { 0: 1 }));
	});
});

describe("result (§4.4 step 5) and logging (§12)", () => {
	test("details shape, kept tail from the projected entries, INFO line on session_compact", async () => {
		const msgs = [user("u1", 1), assistant("a1", 2), user("x".repeat(400), 3), assistant("y".repeat(40), 4)];
		let clock = 1000;
		const run = makeRun({ messages: msgs, keptIndex: 2, now: () => clock });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		const pending = run.compact({ isSplitTurn: true });
		clock += 250;
		const result = await pending;
		const expectedTail = estimateTokens(msgs[2]) + estimateTokens(msgs[3]);
		assert.deepEqual(result, {
			compaction: {
				summary: "Goal: test",
				firstKeptEntryId: "e2",
				tokensBefore: 50123,
				usage: { ...usage0, input: 120, cacheRead: 30000, output: 400 },
				details: { path: "prefix", prompt: 30120, cached: 30000, output: 400, ms: 250, isSplitTurn: true, tailTokens: expectedTail },
			},
		});
		await run.emit("session_compact", { type: "session_compact", fromExtension: true, reason: "threshold", willRetry: false });
		assert.deepEqual(run.log.lines, [
			`INFO pi compaction threshold path=prefix tokensBefore=50123 tail=${expectedTail} splitTurn=true summary=400 cached=30000 ms=250`,
		]);
	});

	test("kept tail over 10240 → Warning, and the summary is still used", async () => {
		const big = user("z".repeat(4 * 10300), 3);
		const msgs = [user("u1", 1), assistant("a1", 2), big, assistant("ok", 4)];
		const run = makeRun({ messages: msgs, keptIndex: 2 });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		const result = (await run.compact()) as any;
		const tail = estimateTokens(big) + estimateTokens(msgs[3]);
		assert.ok(tail > TAIL_BUDGET);
		assert.equal(result.compaction.details.tailTokens, tail);
		assert.deepEqual(run.warnings(), [`WARN kept tail over 10240 tail=${tail}`]);
	});

	test("compaction over 60 s → Warning naming the path", async () => {
		const msgs = conversation();
		let clock = 0;
		const run = makeRun({ messages: msgs, now: () => clock });
		await run.capture(mainPayload(msgs.slice(0, 3)));
		await run.compact({ reason: "overflow" });
		clock += SLOW_COMPACTION_MS + 1;
		await run.emit("session_compact", { type: "session_compact", fromExtension: false, reason: "overflow", willRetry: true });
		assert.deepEqual(run.warnings(), ["WARN fast-path fallback reason=overflow", `WARN compaction over 60 s ms=${SLOW_COMPACTION_MS + 1} path=default`]);
	});
});
