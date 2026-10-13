// Test-only Pi extension for test/real-pi.test.ts. Never shipped or loaded by Fleet.
// It is passed with `-e` BEFORE the Fleet extension, so for one event its handlers
// run first and share the same `ctx` object. FLEET_PI_TEST_EXT_MODE (comma list):
//   noop_command      registers /fleet-test-noop, so a prompt is answered `handled` (A31)
//   mutate_message    changes message 1 of every main request (A18 negative control)
//   usage_unavailable makes ctx.getContextUsage() return undefined for compaction (A19)
//   kept_unresolved   makes the projection's entry ids differ from firstKeptEntryId (A19)

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

export default function fleetTestExtension(pi: ExtensionAPI): void {
	const modes = new Set((process.env.FLEET_PI_TEST_EXT_MODE ?? "").split(",").filter(Boolean));

	if (modes.has("noop_command")) {
		pi.registerCommand("fleet-test-noop", { description: "Test-only no-op", handler: async () => undefined });
	}

	if (modes.has("mutate_message")) {
		pi.on("before_provider_request", (event) => {
			const payload = structuredClone(event.payload) as any;
			const message = payload?.messages?.[1];
			const block = Array.isArray(message?.content) ? message.content.find((b: any) => b?.type === "text") : undefined;
			if (!block || (payload.messages?.length ?? 0) < 3) return undefined;
			block.text = `${block.text} (changed by the test extension)`;
			return payload;
		});
	}

	pi.on("session_before_compact", (_event, ctx) => {
		if (modes.has("usage_unavailable")) {
			Object.defineProperty(ctx, "getContextUsage", { value: () => undefined, configurable: true });
		}
		if (modes.has("kept_unresolved")) {
			const manager = ctx.sessionManager as any;
			const shifted = () => {
				const projection = manager.buildSessionProjection();
				return {
					...projection,
					entries: projection.entries.map((e: any) => ({ ...e, sourceEntry: { ...e.sourceEntry, id: `test-shifted-${e.sourceEntry.id}` } })),
				};
			};
			const proxy = new Proxy(manager, {
				get(target, prop) {
					if (prop === "buildSessionProjection") return shifted;
					const value = Reflect.get(target, prop, target);
					return typeof value === "function" ? value.bind(target) : value;
				},
			});
			Object.defineProperty(ctx, "sessionManager", { value: proxy, configurable: true });
		}
		return undefined;
	});
}
