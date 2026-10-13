// Fleet's Pi extension (phleet #386 §1, §4.4, §4.5), loaded by the pinned Pi 1.0.4 as
//   pi --mode rpc ... -nc -ns -np -ne --no-mcp -e /opt/fleet-pi-extension --tools read,bash,edit,write,<granted mcp__ names>
//
// Pi resolves the directory through package.json `pi.extensions` (./index.ts) and
// loads it with jiti; `@earendil-works/pi-coding-agent` resolves to Pi's own
// bundled module, other imports to this package's node_modules. Pi awaits this
// async factory before the session starts. A throw here makes Pi report "Failed
// to load extension" and exit 1, which is how an invalid fleet-pi.json fails closed.

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { convertToLlm, estimateTokens } from "@earendil-works/pi-coding-agent";
import { registerCompaction, SUMMARY_TIMEOUT_MS } from "./compaction.ts";
import { type FleetPiConfig, loadConfig, testTimeoutOverride } from "./config.ts";
import { stderrLogger } from "./log.ts";
import { connectMcpTools } from "./mcp-tools.ts";

/** Test-only: shortens the 90 s summary timeout. Unset in production. */
export const SUMMARY_TIMEOUT_ENV = "FLEET_PI_SUMMARY_TIMEOUT_MS";
/** Test-only: shortens the tools/call timeout from fleet-pi.json. Unset in production. */
export const TOOL_CALL_TIMEOUT_ENV = "FLEET_PI_TOOL_CALL_TIMEOUT_MS";

export default async function fleetPiExtension(pi: ExtensionAPI): Promise<void> {
	const log = stderrLogger;
	let config: FleetPiConfig;
	try {
		config = loadConfig(process.env);
	} catch (error) {
		log.error(`fleet-pi-extension not loaded: ${error instanceof Error ? error.message : "invalid configuration"}`);
		throw error;
	}

	const summaryTimeout = testTimeoutOverride(process.env[SUMMARY_TIMEOUT_ENV], SUMMARY_TIMEOUT_MS);
	if (summaryTimeout.overridden) log.warn(`test override ${SUMMARY_TIMEOUT_ENV}=${summaryTimeout.ms}`);
	registerCompaction(pi, { convertToLlm, estimateTokens, log, timeoutMs: summaryTimeout.ms });

	const callTimeout = testTimeoutOverride(process.env[TOOL_CALL_TIMEOUT_ENV], config.toolCallTimeoutSeconds * 1000);
	if (callTimeout.overridden) log.warn(`test override ${TOOL_CALL_TIMEOUT_ENV}=${callTimeout.ms}`);
	const bridge = await connectMcpTools({
		configPath: config.mcpConfigPath,
		allowedTools: config.allowedTools,
		maxBytes: config.toolResultMaxBytes,
		callTimeoutMs: callTimeout.ms,
		log,
	});
	// Registered once, before the session starts; never added later (§18).
	for (const tool of bridge.tools) pi.registerTool(tool);
	pi.on("session_shutdown", async (event) => {
		if (event.reason === "quit") await bridge.close();
	});
}
