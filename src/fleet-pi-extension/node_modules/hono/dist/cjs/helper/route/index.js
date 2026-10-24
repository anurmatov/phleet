Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_request_constants = require("../../request/constants.js");
const require_utils_url = require("../../utils/url.js");
//#region src/helper/route/index.ts
/**
* Get matched routes in the handler
*
* @param {Context} c - The context object
* @returns An array of matched routes
*
* @example
* ```ts
* import { matchedRoutes } from 'hono/route'
*
* app.use('*', async function logger(c, next) {
*   await next()
*   matchedRoutes(c).forEach(({ handler, method, path }, i) => {
*     const name = handler.name || (handler.length < 2 ? '[handler]' : '[middleware]')
*     console.log(
*       method,
*       ' ',
*       path,
*       ' '.repeat(Math.max(10 - path.length, 0)),
*       name,
*       i === c.req.routeIndex ? '<- respond from here' : ''
*     )
*   })
* })
* ```
*/
const matchedRoutes = (c) => c.req[require_request_constants.GET_MATCH_RESULT][0].map(([[, route]]) => route);
/**
* Get the route path registered within the handler
*
* @param {Context} c - The context object
* @param {number} index - The index of the route from which to retrieve the path, similar to Array.prototype.at(), where a negative number is the index counted from the end of the matching route. Defaults to the current route index.
* @returns The route path registered within the handler
*
* @example
* ```ts
* import { routePath } from 'hono/route'
*
* app.use('*', (c, next) => {
*   console.log(routePath(c)) // '*'
*   console.log(routePath(c, -1)) // '/posts/:id'
*   return next()
* })
*
* app.get('/posts/:id', (c) => {
*   return c.text(routePath(c)) // '/posts/:id'
* })
* ```
*/
const routePath = (c, index) => matchedRoutes(c).at(index ?? c.req.routeIndex)?.path ?? "";
/**
* Get the basePath of the as-is route specified by routing.
*
* @param {Context} c - The context object
* @param {number} index - The index of the route from which to retrieve the path, similar to Array.prototype.at(), where a negative number is the index counted from the end of the matching route. Defaults to the current route index.
* @returns The basePath of the as-is route specified by routing.
*
* @example
* ```ts
* import { baseRoutePath } from 'hono/route'
*
* const app = new Hono()
*
* const subApp = new Hono()
* subApp.get('/posts/:id', (c) => {
*   return c.text(baseRoutePath(c)) // '/:sub'
* })
*
* app.route('/:sub', subApp)
* ```
*/
const baseRoutePath = (c, index) => matchedRoutes(c).at(index ?? c.req.routeIndex)?.basePath ?? "";
/**
* Get the basePath with embedded parameters
*
* @param {Context} c - The context object
* @param {number} index - The index of the route from which to retrieve the path, similar to Array.prototype.at(), where a negative number is the index counted from the end of the matching route. Defaults to the current route index.
* @returns The basePath with embedded parameters.
*
* @example
* ```ts
* import { basePath } from 'hono/route'
*
* const app = new Hono()
*
* const subApp = new Hono()
* subApp.get('/posts/:id', (c) => {
*   return c.text(basePath(c)) // '/requested-sub-app-path'
* })
*
* app.route('/:sub', subApp)
* ```
*/
const basePathCacheMap = /* @__PURE__ */ new WeakMap();
const basePath = (c, index) => {
	index ??= c.req.routeIndex;
	const cache = basePathCacheMap.get(c) || [];
	if (typeof cache[index] === "string") return cache[index];
	let result;
	const rp = baseRoutePath(c, index);
	if (!/[:*]/.test(rp)) result = rp;
	else {
		const paths = require_utils_url.splitRoutingPath(rp);
		const reqPath = c.req.path;
		let basePathLength = 0;
		for (let i = 0, len = paths.length; i < len; i++) {
			const pattern = require_utils_url.getPattern(paths[i], paths[i + 1]);
			if (pattern) {
				const re = pattern[2] === true || pattern === "*" ? /[^\/]+/ : pattern[2];
				basePathLength += reqPath.substring(basePathLength + 1).match(re)?.[0].length || 0;
			} else basePathLength += paths[i].length;
			basePathLength += 1;
		}
		result = reqPath.substring(0, basePathLength);
	}
	cache[index] = result;
	basePathCacheMap.set(c, cache);
	return result;
};
//#endregion
exports.basePath = basePath;
exports.baseRoutePath = baseRoutePath;
exports.matchedRoutes = matchedRoutes;
exports.routePath = routePath;
