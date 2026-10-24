Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
require("../../router.js");
const require_router_trie_router_router = require("../../router/trie-router/router.js");
require("../../router/trie-router/index.js");
const require_helper_route_index = require("../../helper/route/index.js");
//#region src/middleware/method-not-allowed/index.ts
/**
* Method Not Allowed Middleware for Hono.
*
* Returns a `405 Method Not Allowed` response with an `Allow` header when the request path
* matches a registered route but the request method is not supported.
*
* @param {MethodNotAllowedOptions} options - The options for the middleware.
* @param {Hono} options.app - The Hono instance used by the application.
* @param {MethodNotAllowedHandler} [options.onMethodNotAllowed] - Generates the response, including its `Allow` header.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* app.use(methodNotAllowed({ app }))
*
* app.get('/hello', (c) => c.text('Hello!'))
* app.post('/hello', (c) => c.text('Posted!'))
*
* // PUT /hello -> 405 Method Not Allowed
* // Allow: GET, HEAD, POST
* ```
*
* @example
* ```ts
* app.use(
*   methodNotAllowed({
*     app,
*     onMethodNotAllowed: (c, methods) =>
*       c.json({ error: 'Method Not Allowed' }, 405, { Allow: methods.join(', ') }),
*   })
* )
* ```
*/
const methodNotAllowed = (options) => {
	let methodRouter;
	return async function methodNotAllowed(c, next) {
		const routeIndex = c.req.routeIndex;
		await next();
		if (c.res.status !== 404) return;
		if (c.error) return;
		if (!methodRouter) {
			const methodsByPath = /* @__PURE__ */ new Map();
			for (const route of options.app.routes) {
				if (route.method === "ALL" || route.method === "HEAD") continue;
				const methods = methodsByPath.get(route.path) ?? /* @__PURE__ */ new Set();
				methods.add(route.method);
				if (route.method === "GET") methods.add("HEAD");
				methodsByPath.set(route.path, methods);
			}
			methodRouter = new require_router_trie_router_router.TrieRouter();
			for (const [path, methods] of methodsByPath) methodRouter.add("ALL", path, [...methods]);
		}
		let requestPath = c.req.path;
		const currentRoute = require_helper_route_index.matchedRoutes(c)[routeIndex];
		const sourceRoute = options.app.routes.find((route) => route.handler === methodNotAllowed);
		if (currentRoute && sourceRoute) {
			const currentBasePathParts = require_helper_route_index.basePath(c, routeIndex).split("/").filter(Boolean);
			const sourceBasePathLength = sourceRoute.basePath.split("/").filter(Boolean).length;
			const mountBasePathParts = currentBasePathParts.slice(0, currentBasePathParts.length - sourceBasePathLength);
			if (mountBasePathParts.length > 0) {
				const mountBasePath = `/${mountBasePathParts.join("/")}`;
				requestPath = c.req.path.slice(mountBasePath.length) || "/";
			}
		}
		const allowedMethods = /* @__PURE__ */ new Set();
		for (const [methods] of methodRouter.match("ALL", requestPath)[0]) for (const method of methods) allowedMethods.add(method);
		if (allowedMethods.size === 0 || allowedMethods.has(c.req.method)) return;
		c.res.headers.delete("Allow");
		c.res.headers.delete("Content-Length");
		const methods = [...allowedMethods];
		const allow = methods.join(", ");
		c.res = options.onMethodNotAllowed ? await options.onMethodNotAllowed(c, methods) : c.text("Method Not Allowed", 405, { Allow: allow });
	};
};
//#endregion
exports.methodNotAllowed = methodNotAllowed;
