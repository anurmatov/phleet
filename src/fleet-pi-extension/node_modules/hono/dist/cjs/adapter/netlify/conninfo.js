Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/adapter/netlify/conninfo.ts
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
const getConnInfo = (c) => ({ remote: { address: c.env.context?.ip } });
//#endregion
exports.getConnInfo = getConnInfo;
