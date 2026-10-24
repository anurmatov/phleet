//#region src/adapter/vercel/conninfo.ts
/**
* @deprecated `hono/vercel` will be removed in v5. Install `@hono/vercel` and import from there instead.
*/
const getConnInfo = (c) => ({ remote: { address: c.req.header("x-real-ip") } });
//#endregion
export { getConnInfo };
