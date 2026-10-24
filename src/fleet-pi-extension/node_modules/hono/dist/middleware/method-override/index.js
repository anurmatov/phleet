import { parseBody } from "../../utils/body.js";
//#region src/middleware/method-override/index.ts
const DEFAULT_METHOD_FORM_NAME = "_method";
/**
* Method Override Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/method-override}
*
* @param {MethodOverrideOptions} options - The options for the method override middleware.
* @param {Hono} options.app - The instance of Hono is used in your application.
* @param {string} [options.form=_method] - Form key with a value containing the method name.
* @param {string} [options.header] - Header name with a value containing the method name.
* @param {string} [options.query] - Query parameter key with a value containing the method name.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* // If no options are specified, the value of `_method` in the form,
* // e.g. DELETE, is used as the method.
* app.use('/posts', methodOverride({ app }))
*
* app.delete('/posts', (c) => {
*   // ....
* })
* ```
*/
const methodOverride = (options) => async function methodOverride(c, next) {
	if (c.req.method === "GET") return await next();
	const app = options.app;
	if (!(options.header || options.query)) {
		const contentType = c.req.header("content-type");
		const methodFormName = options.form || DEFAULT_METHOD_FORM_NAME;
		const clonedRequest = c.req.raw.clone();
		const newRequest = clonedRequest.clone();
		if (contentType?.startsWith("multipart/form-data")) {
			const method = (await clonedRequest.formData()).get(methodFormName);
			if (method) {
				const newForm = await newRequest.formData();
				newForm.delete(methodFormName);
				const newHeaders = new Headers(clonedRequest.headers);
				newHeaders.delete("content-type");
				newHeaders.delete("content-length");
				const request = new Request(c.req.url, {
					body: newForm,
					headers: newHeaders,
					method
				});
				return app.fetch(request, c.env, getExecutionCtx(c));
			}
		}
		if (contentType?.startsWith("application/x-www-form-urlencoded")) {
			const params = await parseBody(clonedRequest);
			const method = params[methodFormName];
			if (method) {
				delete params[methodFormName];
				const newParams = new URLSearchParams(params);
				const request = new Request(newRequest, {
					body: newParams,
					method
				});
				return app.fetch(request, c.env, getExecutionCtx(c));
			}
		}
	} else if (options.header) {
		const headerName = options.header;
		const method = c.req.header(headerName);
		if (method) {
			const newHeaders = new Headers(c.req.raw.headers);
			newHeaders.delete(headerName);
			const request = new Request(c.req.raw, {
				headers: newHeaders,
				method
			});
			return app.fetch(request, c.env, getExecutionCtx(c));
		}
	} else if (options.query) {
		const queryName = options.query;
		const method = c.req.query(queryName);
		if (method) {
			const url = new URL(c.req.url);
			url.searchParams.delete(queryName);
			const requestInit = {
				body: c.req.raw.body,
				headers: c.req.raw.headers,
				method,
				duplex: c.req.raw.body ? "half" : void 0
			};
			const request = new Request(url.toString(), requestInit);
			return app.fetch(request, c.env, getExecutionCtx(c));
		}
	}
	await next();
};
const getExecutionCtx = (c) => {
	let executionCtx;
	try {
		executionCtx = c.executionCtx;
	} catch {}
	return executionCtx;
};
//#endregion
export { methodOverride };
