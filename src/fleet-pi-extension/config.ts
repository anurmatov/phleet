// fleet-pi.json: the extension's configuration, written by Fleet into
// $PI_CODING_AGENT_DIR before every Pi start (phleet #386 §4.2). Fail closed: a
// missing file, a missing field or a wrong value throws, Pi reports the
// extension as failed to load and exits 1. Errors name the field, never a value.

import { readFileSync } from "node:fs";
import { isAbsolute, join } from "node:path";

export const CONFIG_FILE_NAME = "fleet-pi.json";
export const AGENT_DIR_ENV = "PI_CODING_AGENT_DIR";

/** §4.4 per-result MCP cap (48 KiB). */
export const TOOL_RESULT_MAX_BYTES = 49152;
/** §4.5 tools/call timeout. */
export const TOOL_CALL_TIMEOUT_SECONDS = 300;
/** §4.4 summary cap S. The tested instruction (G1–G4) is bound to this value; it is never raised. */
export const SUMMARY_MAX_TOKENS = 1280;

export interface FleetPiConfig {
	mcpConfigPath: string;
	allowedTools: string[];
	toolResultMaxBytes: number;
	toolCallTimeoutSeconds: number;
	summaryMaxTokens: number;
}

export class FleetPiConfigError extends Error {
	constructor(message: string) {
		super(message);
		this.name = "FleetPiConfigError";
	}
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function requireExact(raw: Record<string, unknown>, field: string, expected: number): number {
	const value = raw[field];
	if (typeof value !== "number" || !Number.isInteger(value)) {
		throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: ${field} is missing or not an integer`);
	}
	if (value !== expected) {
		throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: ${field} must be ${expected}`);
	}
	return value;
}

/** Validate the parsed JSON. Unknown fields are ignored. */
export function parseConfig(text: string): FleetPiConfig {
	let raw: unknown;
	try {
		raw = JSON.parse(text);
	} catch {
		throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: not valid JSON`);
	}
	if (!isPlainObject(raw)) throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: not a JSON object`);

	const mcpConfigPath = raw.mcpConfigPath;
	if (typeof mcpConfigPath !== "string" || mcpConfigPath.length === 0 || !isAbsolute(mcpConfigPath)) {
		throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: mcpConfigPath is missing or not an absolute path`);
	}
	const allowedTools = raw.allowedTools;
	if (!Array.isArray(allowedTools) || !allowedTools.every((t) => typeof t === "string" && t.length > 0)) {
		throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: allowedTools is missing or not an array of non-empty strings`);
	}
	return {
		mcpConfigPath,
		allowedTools: [...allowedTools],
		toolResultMaxBytes: requireExact(raw, "toolResultMaxBytes", TOOL_RESULT_MAX_BYTES),
		toolCallTimeoutSeconds: requireExact(raw, "toolCallTimeoutSeconds", TOOL_CALL_TIMEOUT_SECONDS),
		summaryMaxTokens: requireExact(raw, "summaryMaxTokens", SUMMARY_MAX_TOKENS),
	};
}

export function configPath(env: NodeJS.ProcessEnv = process.env): string {
	const dir = env[AGENT_DIR_ENV];
	if (typeof dir !== "string" || dir.length === 0) {
		throw new FleetPiConfigError(`${AGENT_DIR_ENV} is not set; cannot locate ${CONFIG_FILE_NAME}`);
	}
	return join(dir, CONFIG_FILE_NAME);
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): FleetPiConfig {
	const path = configPath(env);
	let text: string;
	try {
		text = readFileSync(path, "utf8");
	} catch {
		throw new FleetPiConfigError(`${CONFIG_FILE_NAME}: missing or unreadable in ${AGENT_DIR_ENV}`);
	}
	return parseConfig(text);
}

/**
 * Test-only override of a timeout default, read from an environment variable.
 * Unset keeps the default. It can only shorten a timeout: a value that is not a
 * positive integer, or that exceeds the default, is ignored.
 */
export function testTimeoutOverride(raw: string | undefined, defaultMs: number): { ms: number; overridden: boolean } {
	if (raw === undefined || raw === "") return { ms: defaultMs, overridden: false };
	const value = Number(raw);
	if (!Number.isInteger(value) || value <= 0 || value > defaultMs) return { ms: defaultMs, overridden: false };
	return { ms: value, overridden: value !== defaultMs };
}
