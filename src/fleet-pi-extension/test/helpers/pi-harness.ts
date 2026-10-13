// Spawns the real pinned Pi in RPC mode with the §4.2 files and flags, against the
// Anthropic and MCP stubs. Used only by test/real-pi.test.ts.

import { type ChildProcessWithoutNullStreams, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join } from "node:path";
import { fileURLToPath } from "node:url";
import { firstDifference, INSTRUCTION_SHA256 } from "../../compaction.ts";

/** This package's directory: the dev dependency's Pi lives under it. */
export const PACKAGE_DIR = fileURLToPath(new URL("../..", import.meta.url));
/** The extension Pi loads: FLEET_PI_EXTENSION_DIR (e.g. /opt/fleet-pi-extension in the image), else this package. */
export const EXTENSION_DIR = process.env.FLEET_PI_EXTENSION_DIR || PACKAGE_DIR;
export const TEST_EXTENSION = fileURLToPath(new URL("../fixtures/test-extension.ts", import.meta.url));
export const MODEL_TAG = "fleet-test-model";
/** Fleet's minimum context; the threshold is W − 16384 = 49152. */
export const W = 65536;
export const BUILT_INS = ["read", "bash", "edit", "write"];

export interface PiCommand {
	command: string;
	args: string[];
	source: string;
}

/** PI_BIN, else the pinned dev dependency's bundled CLI; undefined when neither exists. */
export function resolvePi(): PiCommand | undefined {
	const fromEnv = process.env.PI_BIN;
	if (fromEnv) {
		const path = fromEnv.includes("/") ? fromEnv : (process.env.PATH ?? "").split(delimiter).map((d) => join(d, fromEnv)).find((p) => existsSync(p));
		if (!path || !existsSync(path)) return undefined;
		return path.endsWith(".js") ? { command: process.execPath, args: [path], source: "PI_BIN" } : { command: path, args: [], source: "PI_BIN" };
	}
	const cli = join(PACKAGE_DIR, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
	return existsSync(cli) ? { command: process.execPath, args: [cli], source: "devDependency" } : undefined;
}

export interface TestDirs {
	root: string;
	home: string;
	agentDir: string;
	cwd: string;
	systemPrompt: string;
	mcpConfig: string;
	records: string;
	mcpRecords: string;
	cleanup(): void;
}

export function makeDirs(label: string): TestDirs {
	const root = mkdtempSync(join(tmpdir(), `fleet-pi-${label}-`));
	const dirs = {
		root,
		home: join(root, "home"),
		agentDir: join(root, "home", ".pi", "agent"),
		cwd: join(root, "workspace"),
		systemPrompt: join(root, "system-prompt.md"),
		mcpConfig: join(root, "workspace", ".mcp.json"),
		records: join(root, "anthropic-records"),
		mcpRecords: join(root, "mcp-records"),
		cleanup: () => rmSync(root, { recursive: true, force: true }),
	};
	for (const d of [dirs.home, dirs.agentDir, dirs.cwd, dirs.records, dirs.mcpRecords]) mkdirSync(d, { recursive: true });
	writeFileSync(dirs.systemPrompt, "You are a Fleet test agent. Answer briefly.\n");
	writeFileSync(dirs.mcpConfig, JSON.stringify({ mcpServers: {} }));
	return dirs;
}

/** The three §4.2 files, written before every start like PiExecutor does. */
export function writeAgentFiles(dirs: TestDirs, options: { baseUrl: string; allowedTools: string[]; compactionEnabled?: boolean }): void {
	const models = {
		providers: {
			"fleet-local": {
				baseUrl: options.baseUrl,
				api: "anthropic-messages",
				apiKey: "phleet-local-no-auth",
				compat: { supportsStrictTools: false, supportsEagerToolInputStreaming: false },
				models: [
					{
						id: MODEL_TAG,
						name: MODEL_TAG,
						reasoning: true,
						input: ["text"],
						contextWindow: W,
						maxTokens: 16384,
						cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					},
				],
			},
		},
	};
	const settings = {
		compaction: { enabled: options.compactionEnabled ?? true, reserveTokens: 16384, keepRecentTokens: 8192 },
		retry: { enabled: true, maxRetries: 3 },
		steeringMode: "all",
		cacheWarming: "off",
		enableInstallTelemetry: false,
		enableAnalytics: false,
	};
	const fleetPi = {
		mcpConfigPath: dirs.mcpConfig,
		allowedTools: options.allowedTools,
		toolResultMaxBytes: 49152,
		toolCallTimeoutSeconds: 300,
		summaryMaxTokens: 1280,
	};
	writeFileSync(join(dirs.agentDir, "models.json"), JSON.stringify(models));
	writeFileSync(join(dirs.agentDir, "settings.json"), JSON.stringify(settings));
	writeFileSync(join(dirs.agentDir, "fleet-pi.json"), JSON.stringify(fleetPi));
}

export interface StartOptions {
	pi: PiCommand;
	dirs: TestDirs;
	thinking: "off" | "medium";
	grantedTools: string[];
	session?: string;
	/** Test-only extensions, loaded before the Fleet extension. */
	preExtensions?: string[];
	env?: Record<string, string>;
}

/** §4.2 process arguments. */
export function piArgs(options: StartOptions): string[] {
	const pre = (options.preExtensions ?? []).flatMap((p) => ["-e", p]);
	return [
		"--mode",
		"rpc",
		"--offline",
		"--no-approve",
		"--provider",
		"fleet-local",
		"--model",
		MODEL_TAG,
		"--thinking",
		options.thinking,
		"--system-prompt",
		options.dirs.systemPrompt,
		"-nc",
		"-ns",
		"-np",
		"-ne",
		"--no-mcp",
		...pre,
		"-e",
		EXTENSION_DIR,
		"--tools",
		[...BUILT_INS, ...options.grantedTools].join(","),
		...(options.session ? ["--session", options.session] : []),
	];
}

export type PiRecord = Record<string, any>;

export class PiProcess {
	readonly records: PiRecord[] = [];
	readonly stderr: string[] = [];
	private readonly child: ChildProcessWithoutNullStreams;
	private stdoutBuffer = "";
	private stderrBuffer = "";
	private listeners = new Set<() => void>();
	private nextId = 0;
	exitCode: number | null | undefined = undefined;

	private constructor(child: ChildProcessWithoutNullStreams) {
		this.child = child;
		child.stdout.setEncoding("utf8");
		child.stderr.setEncoding("utf8");
		child.stdout.on("data", (chunk: string) => {
			this.stdoutBuffer += chunk;
			let i: number;
			// JSONL framing: split on LF only (never on U+2028/U+2029).
			while ((i = this.stdoutBuffer.indexOf("\n")) >= 0) {
				const line = this.stdoutBuffer.slice(0, i).replace(/\r$/, "");
				this.stdoutBuffer = this.stdoutBuffer.slice(i + 1);
				if (!line.trim()) continue;
				try {
					this.records.push(JSON.parse(line));
				} catch {
					this.records.push({ type: "unparsed", line });
				}
			}
			this.notify();
		});
		child.stderr.on("data", (chunk: string) => {
			this.stderrBuffer += chunk;
			let i: number;
			while ((i = this.stderrBuffer.indexOf("\n")) >= 0) {
				this.stderr.push(this.stderrBuffer.slice(0, i));
				this.stderrBuffer = this.stderrBuffer.slice(i + 1);
			}
			this.notify();
		});
		child.on("exit", (code) => {
			this.exitCode = code;
			this.notify();
		});
	}

	static async start(options: StartOptions): Promise<PiProcess> {
		const env: Record<string, string> = {
			PATH: process.env.PATH ?? "/usr/bin:/bin",
			HOME: options.dirs.home,
			PI_CODING_AGENT_DIR: options.dirs.agentDir,
			PI_OFFLINE: "1",
			PI_TELEMETRY: "0",
			...(process.env.TMPDIR ? { TMPDIR: process.env.TMPDIR } : {}),
			...options.env,
		};
		const child = spawn(options.pi.command, [...options.pi.args, ...piArgs(options)], { cwd: options.dirs.cwd, env, stdio: "pipe" });
		const pi = new PiProcess(child);
		try {
			const state = await pi.request({ type: "get_state" }, 60_000);
			if (!state.success) throw new Error(`get_state failed: ${state.error}`);
		} catch (error) {
			await pi.stop();
			throw new Error(`Pi did not start: ${(error as Error).message}\n${pi.stderr.join("\n")}`);
		}
		return pi;
	}

	private notify(): void {
		for (const listener of [...this.listeners]) listener();
	}

	waitFor(predicate: (r: PiRecord) => boolean, timeoutMs = 60_000, from = 0): Promise<PiRecord> {
		return new Promise((resolve, reject) => {
			const check = () => {
				for (let i = from; i < this.records.length; i++) {
					if (predicate(this.records[i])) {
						cleanup();
						resolve(this.records[i]);
						return true;
					}
				}
				if (this.exitCode !== undefined) {
					cleanup();
					reject(new Error(`Pi exited (${this.exitCode}) while waiting\n${this.stderr.slice(-20).join("\n")}`));
					return true;
				}
				return false;
			};
			const timer = setTimeout(() => {
				cleanup();
				reject(new Error(`timed out after ${timeoutMs} ms waiting for a Pi record\n${this.stderr.slice(-20).join("\n")}`));
			}, timeoutMs);
			const listener = () => void check();
			const cleanup = () => {
				clearTimeout(timer);
				this.listeners.delete(listener);
			};
			this.listeners.add(listener);
			check();
		});
	}

	send(command: Record<string, unknown>): void {
		this.child.stdin.write(`${JSON.stringify(command)}\n`);
	}

	async request(command: Record<string, unknown>, timeoutMs = 60_000): Promise<PiRecord> {
		const id = `t${++this.nextId}`;
		const from = this.records.length;
		this.send({ ...command, id });
		return this.waitFor((r) => r.type === "response" && r.id === id, timeoutMs, from);
	}

	/** Send a prompt; when Pi starts or queues it, wait for `agent_settled`. Returns this turn's records. */
	async prompt(message: string, timeoutMs = 90_000): Promise<{ disposition: string; records: PiRecord[] }> {
		const from = this.records.length;
		const ack = await this.request({ type: "prompt", message }, timeoutMs);
		if (!ack.success) throw new Error(`prompt rejected: ${ack.error}`);
		const disposition = ack.data?.disposition;
		if (disposition === "started" || disposition === "queued") {
			await this.waitFor((r) => r.type === "agent_settled", timeoutMs, from);
		}
		return { disposition, records: this.records.slice(from) };
	}

	async state(): Promise<any> {
		return (await this.request({ type: "get_state" })).data;
	}

	warnings(): string[] {
		return this.stderr.filter((l) => l.startsWith("WARN "));
	}

	infos(): string[] {
		return this.stderr.filter((l) => l.startsWith("INFO "));
	}

	events(type: string): PiRecord[] {
		return this.records.filter((r) => r.type === type);
	}

	async stop(): Promise<void> {
		if (this.exitCode !== undefined) return;
		this.child.stdin.end();
		const exited = new Promise<void>((resolve) => this.child.once("exit", () => resolve()));
		const timer = setTimeout(() => this.child.kill("SIGKILL"), 10_000);
		await exited;
		clearTimeout(timer);
	}
}

// ---------------------------------------------------------------------------
// Stub record analysis

/** Text of the last text block of a message. */
export function lastText(message: any): string | undefined {
	if (typeof message?.content === "string") return message.content;
	const blocks = Array.isArray(message?.content) ? message.content.filter((b: any) => b?.type === "text") : [];
	return blocks.length > 0 ? blocks[blocks.length - 1].text : undefined;
}

export function sha256(text: string): string {
	return createHash("sha256").update(Buffer.from(text, "utf8")).digest("hex");
}

/** A18 shape checks of one summary request against the main request before it; returns failures. */
export function checkPrefixSummary(summary: any, main: any): string[] {
	const failures: string[] = [];
	if (JSON.stringify(summary.system) !== JSON.stringify(main.system)) failures.push("system differs from the preceding main request");
	if (JSON.stringify(summary.tools) !== JSON.stringify(main.tools)) failures.push("tools differ from the preceding main request");
	const diff = firstDifference(main.messages ?? [], summary.messages ?? []);
	if (diff !== -1) failures.push(`messages do not start with the main request's (first difference at ${diff})`);
	const last = summary.messages?.[summary.messages.length - 1];
	if (last?.role !== "user" || sha256(lastText(last) ?? "") !== INSTRUCTION_SHA256) failures.push("last message is not the instruction");
	if (JSON.stringify(summary.thinking) !== JSON.stringify({ type: "disabled" })) failures.push("thinking is not disabled");
	if (summary.max_tokens !== 1280) failures.push("max_tokens is not 1280");
	return failures;
}

// ---------------------------------------------------------------------------
// Session file checks (A21)

export function readSession(file: string): any[] {
	return readFileSync(file, "utf8")
		.split("\n")
		.filter((l) => l.trim())
		.map((l) => JSON.parse(l));
}

/** A21: the latest compaction's first kept entry is not a toolResult and every kept toolResult has its toolCall kept. */
export function checkToolPairing(entries: any[]): string[] {
	const failures: string[] = [];
	const compactions = entries.filter((e) => e.type === "compaction");
	const latest = compactions[compactions.length - 1];
	if (!latest) return ["no compaction entry"];
	const keptIndex = entries.findIndex((e) => e.id === latest.firstKeptEntryId);
	if (keptIndex < 0) return ["firstKeptEntryId not found in the session file"];
	const first = entries[keptIndex];
	if (first.type === "message" && first.message?.role === "toolResult") failures.push("first kept entry is a toolResult");
	const kept = entries.slice(keptIndex).filter((e) => e.type === "message");
	const callIds = new Set(
		kept.flatMap((e) => (e.message?.role === "assistant" ? (e.message.content ?? []).filter((b: any) => b?.type === "toolCall").map((b: any) => b.id) : [])),
	);
	for (const e of kept) {
		if (e.message?.role === "toolResult" && !callIds.has(e.message.toolCallId)) failures.push(`kept toolResult ${e.message.toolCallId} has no kept toolCall`);
	}
	return failures;
}

/** Deterministic filler of about `tokens` estimated tokens (chars/4), in short lines. */
export function filler(tokens: number, tag: string): string {
	const line = `${tag} filler line for the compaction tests, nothing to remember here.\n`;
	return line.repeat(Math.ceil((tokens * 4) / line.length)).slice(0, tokens * 4);
}
