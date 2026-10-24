//#region src/middleware/powered-by/index.ts
/**
* Powered By Middleware for Hono.
*
* @param options - The options for the Powered By Middleware.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* import { poweredBy } from 'hono/powered-by'
*
* const app = new Hono()
*
* app.use(poweredBy()) // With options: poweredBy({ serverName: "My Server" })
* ```
*/
const poweredBy = (options) => {
	return async function poweredBy(c, next) {
		await next();
		c.res.headers.set("X-Powered-By", options?.serverName ?? "Hono");
	};
};
//#endregion
export { poweredBy };
