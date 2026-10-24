import { GetConnInfo } from "../../helper/conninfo/types.js";
//#region src/adapter/netlify/conninfo.d.ts
/**
 * Get connection information from Netlify
 * @param c - Context
 * @returns Connection information including remote address
 * @example
 * ```ts
 * import { Hono } from 'hono'
 * import { handle, getConnInfo } from 'hono/netlify'
 *
 * const app = new Hono()
 *
 * app.get('/', (c) => {
 *   const info = getConnInfo(c)
 *   return c.text(`Your IP: ${info.remote.address}`)
 * })
 *
 * export default handle(app)
 * ```
 * @deprecated `hono/netlify` will be removed in v5. Install `@hono/netlify` and import from there instead.
 */
export declare const getConnInfo: GetConnInfo;
//#endregion