Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_http_exception = require("../../http-exception.js");
//#region src/middleware/body-limit/index.ts
const ERROR_MESSAGE = "Payload Too Large";
/**
* Body Limit Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/body-limit}
*
* @param {BodyLimitOptions} options - The options for the body limit middleware.
* @param {number} options.maxSize - The maximum body size allowed.
* @param {OnError} [options.onError] - The error handler to be invoked if the specified body size is exceeded.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* app.post(
*   '/upload',
*   bodyLimit({
*     maxSize: 50 * 1024, // 50kb
*     onError: (c) => {
*       return c.text('overflow :(', 413)
*     },
*   }),
*   async (c) => {
*     const body = await c.req.parseBody()
*     if (body['file'] instanceof File) {
*       console.log(`Got file sized: ${body['file'].size}`)
*     }
*     return c.text('pass :)')
*   }
* )
* ```
*/
const bodyLimit = (options) => {
	const onError = options.onError || (() => {
		const res = new Response(ERROR_MESSAGE, { status: 413 });
		throw new require_http_exception.HTTPException(413, { res });
	});
	const maxSize = options.maxSize;
	return async function bodyLimit(c, next) {
		if (!c.req.raw.body) return next();
		const hasTransferEncoding = c.req.raw.headers.has("transfer-encoding");
		if (c.req.raw.headers.has("content-length") && !hasTransferEncoding) return parseInt(c.req.raw.headers.get("content-length") || "0", 10) > maxSize ? onError(c) : next();
		let size = 0;
		const chunks = [];
		const rawReader = c.req.raw.body.getReader();
		for (;;) {
			const { done, value } = await rawReader.read();
			if (done) break;
			size += value.length;
			if (size > maxSize) return onError(c);
			chunks.push(value);
		}
		const requestInit = {
			body: new ReadableStream({ start(controller) {
				for (const chunk of chunks) controller.enqueue(chunk);
				controller.close();
			} }),
			duplex: "half"
		};
		c.req.raw = new Request(c.req.raw, requestInit);
		return next();
	};
};
//#endregion
exports.bodyLimit = bodyLimit;
