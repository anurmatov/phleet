//#region src/adapter/cloudflare-pages/conninfo.ts
/**
* Get connection information from Cloudflare Pages
* @param c - Context
* @returns Connection information including remote address
* @example
* ```ts
* import { Hono } from 'hono'
* import { handle, getConnInfo } from 'hono/cloudflare-pages'
*
* const app = new Hono()
*
* app.get('/', (c) => {
*   const info = getConnInfo(c)
*   return c.text(`Your IP: ${info.remote.address}`)
* })
*
* export const onRequest = handle(app)
* ```
* @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
*/
const getConnInfo = (c) => ({ remote: { address: c.req.header("cf-connecting-ip") } });
//#endregion
export { getConnInfo };
