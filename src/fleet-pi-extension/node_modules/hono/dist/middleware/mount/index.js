import { routePath } from "../../helper/route/index.js";
//#region src/middleware/mount/index.ts
const defaultReplaceRequest = (c) => {
	const pathPrefix = routePath(c).replace(/\/\*$/, "");
	const url = new URL(c.req.raw.url);
	url.pathname = c.req.path.slice(pathPrefix.length) || "/";
	return new Request(url, c.req.raw);
};
const defaultGetOptions = (c) => {
	let executionContext = void 0;
	try {
		executionContext = c.executionCtx;
	} catch {}
	return [c.env, executionContext];
};
/**
* `mount()` allows you to mount applications built with other frameworks into your Hono application.
*
* @see {@link https://hono.dev/docs/api/hono#mount}
*
* @param {Function} applicationHandler - other Request Handler
* @param {MountOptions} [options] - options of `mount()`
* @returns {MiddlewareHandler} handler to register with `app.all()`
*
* @example
* ```ts
* import { Router as IttyRouter } from 'itty-router'
* import { Hono } from 'hono'
* import { mount } from 'hono/mount'
* // Create itty-router application
* const ittyRouter = IttyRouter()
* // GET /itty-router/hello
* ittyRouter.get('/hello', () => new Response('Hello from itty-router'))
*
* const app = new Hono()
* app.all('/itty-router/*', mount(ittyRouter.handle))
* ```
*
* @example
* ```ts
* const app = new Hono()
* // Send the request to another application without modification.
* app.all('/app/*', mount(anotherApp, {
*   replaceRequest: (req) => req,
* }))
* ```
*/
const mount = (applicationHandler, options) => {
	let replaceRequest;
	let optionHandler;
	if (options) {
		if (typeof options === "function") optionHandler = options;
		else {
			optionHandler = options.optionHandler;
			if (options.replaceRequest === false) replaceRequest = (request) => request;
			else replaceRequest = options.replaceRequest;
		}
	}
	const getOptions = optionHandler ? (c) => {
		const options = optionHandler(c);
		return Array.isArray(options) ? options : [options];
	} : defaultGetOptions;
	return async (c, next) => {
		const res = await applicationHandler(replaceRequest ? replaceRequest(c.req.raw) : defaultReplaceRequest(c), ...getOptions(c));
		if (res) return res;
		await next();
	};
};
//#endregion
export { mount };
