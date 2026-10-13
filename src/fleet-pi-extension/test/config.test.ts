// fleet-pi.json validation (phleet #386 §4.2): fail closed, errors name fields, never values.

import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, test } from "node:test";
import { FleetPiConfigError, loadConfig, parseConfig, testTimeoutOverride } from "../config.ts";

const valid = {
	mcpConfigPath: "/workspace/.mcp.json",
	allowedTools: ["mcp__fleet-memory__memory_get", "Bash"],
	toolResultMaxBytes: 49152,
	toolCallTimeoutSeconds: 300,
	summaryMaxTokens: 1280,
};

describe("fleet-pi.json", () => {
	test("the §4.2 file parses", () => {
		assert.deepEqual(parseConfig(JSON.stringify(valid)), valid);
	});

	const broken: Array<[string, unknown, RegExp]> = [
		["not JSON", "{", /not valid JSON/],
		["an array", "[]", /not a JSON object/],
		["mcpConfigPath missing", { ...valid, mcpConfigPath: undefined }, /mcpConfigPath/],
		["mcpConfigPath relative", { ...valid, mcpConfigPath: ".mcp.json" }, /mcpConfigPath/],
		["allowedTools missing", { ...valid, allowedTools: undefined }, /allowedTools/],
		["allowedTools with a number", { ...valid, allowedTools: ["a", 1] }, /allowedTools/],
		["toolResultMaxBytes raised", { ...valid, toolResultMaxBytes: 65536 }, /toolResultMaxBytes must be 49152/],
		["toolCallTimeoutSeconds missing", { ...valid, toolCallTimeoutSeconds: undefined }, /toolCallTimeoutSeconds/],
		["summaryMaxTokens raised", { ...valid, summaryMaxTokens: 2048 }, /summaryMaxTokens must be 1280/],
		["summaryMaxTokens a string", { ...valid, summaryMaxTokens: "1280" }, /summaryMaxTokens/],
	];
	for (const [label, value, pattern] of broken) {
		test(`${label} → fails closed`, () => {
			const text = typeof value === "string" ? value : JSON.stringify(value);
			assert.throws(() => parseConfig(text), (e: unknown) => e instanceof FleetPiConfigError && pattern.test(e.message));
		});
	}

	test("errors never echo a value", () => {
		try {
			parseConfig(JSON.stringify({ ...valid, mcpConfigPath: "relative/secret-path" }));
			assert.fail("expected a throw");
		} catch (e) {
			assert.ok(!(e as Error).message.includes("secret-path"));
		}
	});

	test("loadConfig reads $PI_CODING_AGENT_DIR/fleet-pi.json and fails closed without it", () => {
		const dir = mkdtempSync(join(tmpdir(), "fleet-pi-config-"));
		try {
			assert.throws(() => loadConfig({}), /PI_CODING_AGENT_DIR is not set/);
			assert.throws(() => loadConfig({ PI_CODING_AGENT_DIR: dir }), /missing or unreadable/);
			writeFileSync(join(dir, "fleet-pi.json"), JSON.stringify(valid));
			assert.deepEqual(loadConfig({ PI_CODING_AGENT_DIR: dir }), valid);
		} finally {
			rmSync(dir, { recursive: true, force: true });
		}
	});

	test("test timeout overrides can only shorten a default", () => {
		assert.deepEqual(testTimeoutOverride(undefined, 90_000), { ms: 90_000, overridden: false });
		assert.deepEqual(testTimeoutOverride("500", 90_000), { ms: 500, overridden: true });
		assert.deepEqual(testTimeoutOverride("900000", 90_000), { ms: 90_000, overridden: false });
		assert.deepEqual(testTimeoutOverride("-1", 90_000), { ms: 90_000, overridden: false });
		assert.deepEqual(testTimeoutOverride("abc", 90_000), { ms: 90_000, overridden: false });
	});
});
