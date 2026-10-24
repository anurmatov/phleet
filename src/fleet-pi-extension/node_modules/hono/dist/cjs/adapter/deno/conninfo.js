Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/adapter/deno/conninfo.ts
/**
* Get conninfo with Deno
* @param c Context
* @returns ConnInfo
* @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
*/
const getConnInfo = (c) => {
	const { remoteAddr } = c.env;
	return { remote: {
		address: remoteAddr.hostname,
		port: remoteAddr.port,
		transport: remoteAddr.transport
	} };
};
//#endregion
exports.getConnInfo = getConnInfo;
