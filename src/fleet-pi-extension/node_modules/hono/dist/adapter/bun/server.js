//#region src/adapter/bun/server.ts
/**
* Get Bun Server Object from Context
* @template T - The type of Bun Server
* @param c Context
* @returns Bun Server
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const getBunServer = (c) => "server" in c.env ? c.env.server : c.env;
//#endregion
export { getBunServer };
