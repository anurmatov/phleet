//#region src/middleware/pretty-json/index.ts
const jsonContentTypeRegex = /^application\/(?:[a-z0-9._-]+\+)?json(?=$|[;\s])/i;
/**
* Pretty JSON Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/pretty-json}
*
* @param options - The options for the pretty JSON middleware.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* app.use(prettyJSON()) // With options: prettyJSON({ space: 4 })
* app.get('/', (c) => {
*   return c.json({ message: 'Hono!' })
* })
* ```
*/
const prettyJSON = (options) => {
	const targetQuery = options?.query ?? "pretty";
	return async function prettyJSON(c, next) {
		const pretty = options?.force || c.req.query(targetQuery) || c.req.query(targetQuery) === "";
		await next();
		const contentType = c.res.headers.get("Content-Type");
		if (pretty && contentType && jsonContentTypeRegex.test(contentType)) {
			let obj;
			try {
				obj = await c.res.clone().json();
			} catch {
				return;
			}
			c.res = new Response(JSON.stringify(obj, null, options?.space ?? 2), c.res);
			c.res.headers.delete("Content-Length");
		}
	};
};
//#endregion
export { prettyJSON };
