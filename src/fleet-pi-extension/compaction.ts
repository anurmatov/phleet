// Prefix-reusing compaction for pinned Pi 1.0.4 (phleet #386 §4.4).
//
// Pi's default summary is a separate request with its own system prompt and a
// serialized history, so a local server prefills the whole conversation cold.
// The fast path instead sends the conversation exactly as the last main request
// did, plus one user message with INSTRUCTION, with `system` and `tools` copied
// from that captured request: to the server it looks like an ordinary next turn.
// Any doubt returns `undefined`, and Pi runs its default summary.

import type {
	ContextUsage,
	ExtensionAPI,
	ExtensionContext,
	SessionBeforeCompactEvent,
	SessionBeforeCompactResult,
	SessionCompactEvent,
	SessionCompactFailedEvent,
} from "@earendil-works/pi-coding-agent";
import { SUMMARY_MAX_TOKENS } from "./config.ts";
import type { Logger } from "./log.ts";

/** Summary cap S (tokens), thinking off. */
export const S = SUMMARY_MAX_TOKENS;
/** The summary call is abandoned after this long. */
export const SUMMARY_TIMEOUT_MS = 90_000;
/** Eligibility: tokens + SIZE_MARGIN + S ≤ W − WINDOW_MARGIN. */
export const SIZE_MARGIN = 600;
export const WINDOW_MARGIN = 1024;
/** L2/L2c assume a kept tail of at most this many tokens (§4.4 tail budget). */
export const TAIL_BUDGET = 10240;
/** L1 budget for compaction_start → compaction_end. */
export const SLOW_COMPACTION_MS = 60_000;

/**
 * §4.4 step 4 (P1, tested 2026-10-13). Never change, paraphrase or lengthen it:
 * the G1–G4 pass is bound to these exact 525 ASCII bytes, SHA-256
 * b755fed8758856edf45a96afd398a23a4599fe690cd8aecb5a88ea736fc9e4be, no trailing newline.
 */
export const INSTRUCTION =
	"Do not call tools. Summarize the conversation so far under exactly these headings: Goal / Constraints / Progress / Key decisions / Next steps / Critical context. Copy verbatim every value the user explicitly asked to keep and every identifier, path, number and command still in use. Carry forward the essential facts of earlier summaries. Omit repetitive or historical detail that is not needed to continue; do not enumerate repeated or completed items one by one, state them once in aggregate. Plain text, at most 300 words.";

export const INSTRUCTION_SHA256 = "b755fed8758856edf45a96afd398a23a4599fe690cd8aecb5a88ea736fc9e4be";

/** Eligibility refusals (§4.4 step 1). */
export type EligibilityReason = "overflow" | "no_capture" | "usage_unavailable" | "size" | "kept_unresolved";
/** Response refusals (§4.4 step 5); any other stopReason is reported verbatim. */
export type ResponseReason = "toolUse" | "length" | "error" | "aborted" | "empty" | "timeout";

export interface PrefixDetails {
	path: "prefix";
	prompt: number;
	cached: number;
	output: number;
	ms: number;
	isSplitTurn: boolean;
	tailTokens: number;
}

/** The Anthropic Messages request body as Pi builds it (`before_provider_request` payload). */
interface MessagesPayload {
	messages?: unknown[];
	system?: unknown;
	tools?: unknown;
	[key: string]: unknown;
}

export interface CompactionDeps {
	/** Pi's `convertToLlm` (coding-agent/src/index.ts). */
	convertToLlm: (messages: any[]) => any[];
	/** Pi's `estimateTokens` (chars/4 heuristic) used for `tailTokens`. */
	estimateTokens: (message: any) => number;
	log: Logger;
	/** Summary call timeout; defaults to SUMMARY_TIMEOUT_MS. */
	timeoutMs?: number;
	now?: () => number;
}

type HookHost = Pick<ExtensionAPI, "on">;

/** Deep copy without `cache_control` keys, which move with the conversation's last message. */
export function stripCacheControl(value: unknown): unknown {
	if (Array.isArray(value)) return value.map(stripCacheControl);
	if (value !== null && typeof value === "object") {
		const out: Record<string, unknown> = {};
		for (const [key, inner] of Object.entries(value)) {
			if (key !== "cache_control") out[key] = stripCacheControl(inner);
		}
		return out;
	}
	return value;
}

/** Structural equality of JSON-like values; object key order is irrelevant, `undefined` keys are absent. */
export function deepEqual(a: unknown, b: unknown): boolean {
	if (a === b) return true;
	if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
	if (Array.isArray(a) !== Array.isArray(b)) return false;
	if (Array.isArray(a)) {
		const bb = b as unknown[];
		return a.length === bb.length && a.every((item, i) => deepEqual(item, bb[i]));
	}
	const ao = a as Record<string, unknown>;
	const bo = b as Record<string, unknown>;
	const ak = Object.keys(ao).filter((k) => ao[k] !== undefined);
	const bk = Object.keys(bo).filter((k) => bo[k] !== undefined);
	return ak.length === bk.length && ak.every((k) => Object.hasOwn(bo, k) && deepEqual(ao[k], bo[k]));
}

/**
 * Index of the first message of `prev` that `next` does not repeat (ignoring
 * `cache_control`), or -1 when `prev` is a prefix of `next`.
 */
export function firstDifference(prev: readonly unknown[], next: readonly unknown[]): number {
	for (let i = 0; i < prev.length; i++) {
		if (i >= next.length || !deepEqual(stripCacheControl(prev[i]), stripCacheControl(next[i]))) return i;
	}
	return -1;
}

function asCount(value: unknown): number {
	return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

interface PendingCompaction {
	startedAt: number;
	details?: PrefixDetails;
	tokensBefore?: number;
}

/**
 * Register the capture and the fast path. Returns nothing; all effects go
 * through Pi's hooks and the logger.
 */
export function registerCompaction(pi: HookHost, deps: CompactionDeps): void {
	const log = deps.log;
	const now = deps.now ?? (() => performance.now());
	const timeoutMs = deps.timeoutMs ?? SUMMARY_TIMEOUT_MS;
	let lastMain: MessagesPayload | undefined;
	let pending: PendingCompaction | undefined;

	// Capture only. This fires through the agent's own onPayload (coding-agent/src/core/sdk.ts),
	// never for the summary call below, so a summary never overwrites lastMain. Returning
	// undefined leaves the payload unchanged.
	pi.on("before_provider_request", (event) => {
		try {
			lastMain = structuredClone(event.payload) as MessagesPayload;
		} catch {
			// An uncopyable payload leaves no capture (fail closed: the next compaction is no_capture).
			lastMain = undefined;
		}
		return undefined;
	});

	const fallback = (reason: string): undefined => {
		log.warn(`fast-path fallback reason=${reason}`);
		return undefined;
	};

	const fastPath = async (
		event: SessionBeforeCompactEvent,
		ctx: ExtensionContext,
		entry: PendingCompaction,
	): Promise<SessionBeforeCompactResult | undefined> => {
		// Step 1: eligibility. Nothing is estimated.
		if (event.reason === "overflow") return fallback("overflow");
		const main = lastMain;
		if (!main) return fallback("no_capture");
		const usage: ContextUsage | undefined = ctx.getContextUsage();
		if (
			!usage ||
			typeof usage.tokens !== "number" ||
			!Number.isFinite(usage.tokens) ||
			typeof usage.contextWindow !== "number" ||
			!(usage.contextWindow > 0)
		) {
			return fallback("usage_unavailable");
		}
		if (usage.tokens + SIZE_MARGIN + S > usage.contextWindow - WINDOW_MARGIN) return fallback("size");

		const preparation = event.preparation;
		const projection = ctx.sessionManager.buildSessionProjection();
		const keptIndex = projection.entries.findIndex((e) => e.sourceEntry?.id === preparation.firstKeptEntryId);
		// Never guess the tail by message count or position.
		if (keptIndex < 0) return fallback("kept_unresolved");
		const kept = projection.entries.slice(keptIndex).flatMap((e) => e.messages);
		const tailTokens = kept.reduce((sum: number, m) => sum + deps.estimateTokens(m), 0);
		if (tailTokens > TAIL_BUDGET) log.warn(`kept tail over ${TAIL_BUDGET} tail=${tailTokens}`);

		const model = ctx.model;
		if (!model) return fallback("error");

		// Step 2: the request. The conversation as Pi serializes it, plus one user message.
		const messages = [
			...deps.convertToLlm(projection.messages),
			{ role: "user", content: [{ type: "text", text: INSTRUCTION }], timestamp: Date.now() },
		];
		const prevMessages = Array.isArray(main.messages) ? main.messages : [];
		const onPayload = (payload: unknown): MessagesPayload => {
			const p = (payload ?? {}) as MessagesPayload;
			const i = firstDifference(prevMessages, Array.isArray(p.messages) ? p.messages : []);
			// Logged, and the request is still sent.
			if (i !== -1) log.warn(`summary request does not extend the last request (first difference at message ${i})`);
			const next: MessagesPayload = { ...p, thinking: { type: "disabled" }, max_tokens: S };
			// system and tools come only from the capture, never from getSystemPrompt()/getAllTools().
			if (main.system === undefined) delete next.system;
			else next.system = structuredClone(main.system);
			if (main.tools === undefined) delete next.tools;
			else next.tools = structuredClone(main.tools);
			return next;
		};

		if (event.signal?.aborted) return fallback("aborted");
		const controller = new AbortController();
		const onOuterAbort = () => controller.abort();
		event.signal?.addEventListener("abort", onOuterAbort, { once: true });
		let timedOut = false;
		const timer = setTimeout(() => {
			timedOut = true;
			controller.abort();
		}, timeoutMs);
		const t0 = now();
		let msg: any;
		try {
			msg = await ctx.modelRegistry.complete(model, { messages } as any, {
				maxTokens: S,
				signal: controller.signal,
				onPayload,
			});
		} catch {
			return fallback(timedOut ? "timeout" : controller.signal.aborted ? "aborted" : "error");
		} finally {
			clearTimeout(timer);
			event.signal?.removeEventListener("abort", onOuterAbort);
		}
		const ms = Math.round(now() - t0);
		if (timedOut) return fallback("timeout");

		// Step 5: accept only a normalized "stop" with text and no tool call. "length" is
		// never accepted, even with text: a truncated summary always falls back.
		const blocks: any[] = Array.isArray(msg?.content) ? msg.content : [];
		if (blocks.some((b) => b?.type === "toolCall") || msg?.stopReason === "toolUse") return fallback("toolUse");
		if (msg?.stopReason !== "stop") return fallback(String(msg?.stopReason));
		const summary = blocks
			.filter((b) => b?.type === "text" && typeof b.text === "string")
			.map((b) => b.text as string)
			.join("")
			.trim();
		if (!summary) return fallback("empty");

		const cached = asCount(msg.usage?.cacheRead);
		const details: PrefixDetails = {
			path: "prefix",
			prompt: asCount(msg.usage?.input) + cached + asCount(msg.usage?.cacheWrite),
			cached,
			output: asCount(msg.usage?.output),
			ms,
			// Step 3: one summary covers the whole conversation, split turn included.
			isSplitTurn: preparation.isSplitTurn === true,
			tailTokens,
		};
		entry.details = details;
		entry.tokensBefore = preparation.tokensBefore;
		return {
			compaction: {
				summary,
				firstKeptEntryId: preparation.firstKeptEntryId,
				tokensBefore: preparation.tokensBefore,
				usage: msg.usage,
				details,
			},
		};
	};

	pi.on("session_before_compact", async (event, ctx) => {
		const entry: PendingCompaction = { startedAt: now() };
		pending = entry;
		try {
			return await fastPath(event, ctx, entry);
		} catch {
			// Anything unexpected falls back to Pi's default summary, with the Warning.
			entry.details = undefined;
			return fallback("error");
		}
	});

	const finish = (fromExtension: boolean, reason: string, succeeded: boolean): void => {
		const entry = pending;
		pending = undefined;
		if (!entry) return;
		const totalMs = Math.round(now() - entry.startedAt);
		if (succeeded && fromExtension && entry.details) {
			const d = entry.details;
			log.info(
				`pi compaction ${reason} path=prefix tokensBefore=${asCount(entry.tokensBefore)} tail=${d.tailTokens} splitTurn=${d.isSplitTurn} summary=${d.output} cached=${d.cached} ms=${d.ms}`,
			);
		}
		if (totalMs > SLOW_COMPACTION_MS) {
			log.warn(`compaction over 60 s ms=${totalMs} path=${succeeded && fromExtension ? "prefix" : "default"}`);
		}
	};
	pi.on("session_compact", (event: SessionCompactEvent) => finish(event.fromExtension, event.reason, true));
	pi.on("session_compact_failed", (event: SessionCompactFailedEvent) => finish(event.fromExtension, event.reason, false));
}
