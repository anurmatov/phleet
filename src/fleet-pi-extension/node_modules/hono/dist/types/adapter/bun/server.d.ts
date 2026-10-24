import { Context } from "../../context.js";
//#region src/adapter/bun/server.d.ts
/**
 * Get Bun Server Object from Context
 * @template T - The type of Bun Server
 * @param c Context
 * @returns Bun Server
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export declare const getBunServer: <T>(c: Context) => T | undefined;
//#endregion