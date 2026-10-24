Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_bun_server = require("./server.js");
//#region src/adapter/bun/conninfo.ts
/**
* Get ConnInfo with Bun
* @param c Context
* @returns ConnInfo
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const getConnInfo = (c) => {
	const server = require_adapter_bun_server.getBunServer(c);
	if (!server) throw new TypeError("env has to include the 2nd argument of fetch.");
	if (typeof server.requestIP !== "function") throw new TypeError("server.requestIP is not a function.");
	const info = server.requestIP(c.req.raw);
	if (!info) return { remote: {} };
	return { remote: {
		address: info.address,
		addressType: info.family === "IPv6" || info.family === "IPv4" ? info.family : void 0,
		port: info.port
	} };
};
//#endregion
exports.getConnInfo = getConnInfo;
