Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/adapter/cloudflare-workers/conninfo.ts
/**
* @deprecated `hono/cloudflare-workers` will be removed in v5. Install `@hono/cloudflare-workers` and import from there instead.
*/
const getConnInfo = (c) => ({ remote: { address: c.req.header("cf-connecting-ip") } });
//#endregion
exports.getConnInfo = getConnInfo;
