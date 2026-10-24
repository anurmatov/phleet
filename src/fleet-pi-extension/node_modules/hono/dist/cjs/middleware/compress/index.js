Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_accept = require("../../utils/accept.js");
const require_utils_compress = require("../../utils/compress.js");
//#region src/middleware/compress/index.ts
const ENCODING_TYPES = ["gzip", "deflate"];
const cacheControlNoTransformRegExp = /(?:^|,)\s*?no-transform\s*?(?:,|$)/i;
const selectEncoding = (header, candidates) => {
	if (header === void 0) return;
	const accepts = require_utils_accept.parseAccept(header);
	const wildcardQ = accepts.find((a) => a.type === "*")?.q;
	let best;
	for (const enc of candidates) {
		const explicit = accepts.find((a) => a.type.toLowerCase() === enc);
		const q = explicit ? explicit.q : wildcardQ ?? 0;
		if (q === 1) return enc;
		else if (q > 0 && (!best || q > best.q)) best = {
			encoding: enc,
			q
		};
	}
	return best?.encoding;
};
const varyAcceptEncodingRegExp = /(?:^|,)\s*accept-encoding\s*(?:,|$)/i;
/**
* Compress Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/compress}
*
* @param {CompressionOptions} [options] - The options for the compress middleware.
* @param {'gzip' | 'deflate'} [options.encoding] - The compression scheme to allow for response compression. Either 'gzip' or 'deflate'. If not defined, both are allowed and will be used based on the Accept-Encoding header. 'gzip' is prioritized if this option is not provided and the client provides both in the Accept-Encoding header.
* @param {number} [options.threshold=1024] - The minimum size in bytes to compress. Defaults to 1024 bytes.
* @param {RegExp | Function} [options.contentTypeFilter=COMPRESSIBLE_CONTENT_TYPE_REGEX] - A RegExp or function to determine if the response Content-Type should be compressed.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* app.use(compress())
*
* // Compress only JSON responses
* app.use(compress({ contentTypeFilter: /^application\/json/ }))
*
* // Compress based on custom Content-Type logic
* app.use(compress({ contentTypeFilter: (type) => COMPRESSIBLE_CONTENT_TYPE_REGEX.test(type) || type === "application/x-myformat" }))
* ```
*/
const compress = (options) => {
	const threshold = options?.threshold ?? 1024;
	const candidates = options?.encoding ? [options.encoding] : ENCODING_TYPES;
	const contentTypeFilter = options?.contentTypeFilter ?? require_utils_compress.COMPRESSIBLE_CONTENT_TYPE_REGEX;
	const shouldCompress = typeof contentTypeFilter === "function" ? (res) => {
		const type = res.headers.get("Content-Type");
		return type && contentTypeFilter(type);
	} : (res) => {
		const type = res.headers.get("Content-Type");
		return type && contentTypeFilter.test(type);
	};
	return async function compress(ctx, next) {
		await next();
		const contentLength = ctx.res.headers.get("Content-Length");
		if (ctx.res.status === 206 || ctx.res.headers.has("Content-Encoding") || ctx.res.headers.has("Transfer-Encoding") || ctx.req.method === "HEAD" || contentLength && Number(contentLength) < threshold || !shouldCompress(ctx.res) || !shouldTransform(ctx.res)) return;
		const current = ctx.res.headers.get("Vary");
		if (current !== "*" && !(current && varyAcceptEncodingRegExp.test(current))) ctx.header("Vary", current ? `${current}, Accept-Encoding` : "Accept-Encoding");
		const accepted = ctx.req.header("Accept-Encoding");
		const encoding = selectEncoding(accepted, candidates);
		if (!encoding || !ctx.res.body) return;
		const stream = new CompressionStream(encoding);
		ctx.res = new Response(ctx.res.body.pipeThrough(stream), ctx.res);
		ctx.res.headers.delete("Content-Length");
		ctx.res.headers.set("Content-Encoding", encoding);
		const etag = ctx.res.headers.get("ETag");
		if (etag && !etag.startsWith("W/")) ctx.res.headers.set("ETag", `W/${etag}`);
	};
};
const shouldTransform = (res) => {
	const cacheControl = res.headers.get("Cache-Control");
	return !cacheControl || !cacheControlNoTransformRegExp.test(cacheControl);
};
//#endregion
exports.COMPRESSIBLE_CONTENT_TYPE_REGEX = require_utils_compress.COMPRESSIBLE_CONTENT_TYPE_REGEX;
exports.compress = compress;
