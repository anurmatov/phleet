//#region src/helper/adapter/index.ts
const env = (c, runtime) => {
	const globalEnv = globalThis?.process?.env;
	runtime ??= getRuntimeKey();
	return {
		bun: () => globalEnv,
		node: () => globalEnv,
		"edge-light": () => globalEnv,
		deno: () => {
			return Deno.env.toObject();
		},
		workerd: () => c.env,
		fastly: () => ({}),
		other: () => ({})
	}[runtime]();
};
const knownUserAgents = {
	deno: "Deno",
	bun: "Bun",
	workerd: "Cloudflare-Workers",
	node: "Node.js"
};
const getRuntimeKey = () => {
	const global = globalThis;
	if (typeof navigator !== "undefined" && typeof navigator.userAgent === "string") {
		for (const [runtimeKey, userAgent] of Object.entries(knownUserAgents)) if (checkUserAgentEquals(userAgent)) return runtimeKey;
	}
	if (typeof global?.EdgeRuntime === "string") return "edge-light";
	if (global?.fastly !== void 0) return "fastly";
	if (global?.process?.release?.name === "node") return "node";
	return "other";
};
const checkUserAgentEquals = (platform) => {
	return navigator.userAgent.startsWith(platform);
};
//#endregion
export { checkUserAgentEquals, env, getRuntimeKey, knownUserAgents };
