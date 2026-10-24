Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_url = require("../../utils/url.js");
const require_utils_compress = require("../../utils/compress.js");
const require_utils_mime = require("../../utils/mime.js");
const require_middleware_serve_static_path = require("./path.js");
//#region src/middleware/serve-static/index.ts
const ENCODINGS = {
	br: ".br",
	zstd: ".zst",
	gzip: ".gz"
};
const ENCODINGS_ORDERED_KEYS = Object.keys(ENCODINGS);
const DEFAULT_DOCUMENT = "index.html";
/**
* This middleware is not directly used by the user. Create a wrapper specifying `getContent()` by the environment such as Deno or Bun.
*/
const serveStatic = (options) => {
	const root = options.root ?? "./";
	const optionPath = options.path;
	const join = options.join ?? require_middleware_serve_static_path.defaultJoin;
	return async (c, next) => {
		if (c.finalized) return next();
		let filename;
		if (options.path) filename = options.path;
		else try {
			if (!options.allowPercentInPath && c.req.path.includes("%")) throw new Error();
			filename = require_utils_url.tryDecodeURI(c.req.path);
			if (/(?:^|[\/\\])\.{1,2}(?:$|[\/\\])|[\/\\]{2,}|\\/.test(filename)) throw new Error();
		} catch {
			await options.onNotFound?.(c.req.path, c);
			return next();
		}
		let path = join(root, !optionPath && options.rewriteRequestPath ? options.rewriteRequestPath(filename) : filename);
		if (options.isDir && await options.isDir(path)) path = join(path, DEFAULT_DOCUMENT);
		const getContent = options.getContent;
		let content = await getContent(path, c);
		if (content instanceof Response) return c.newResponse(content.body, content);
		if (content != null) {
			const mimeType = options.mimes && require_utils_mime.getMimeType(path, options.mimes) || require_utils_mime.getMimeType(path);
			c.header("Content-Type", mimeType || "application/octet-stream");
			if (options.precompressed && (!mimeType || require_utils_compress.COMPRESSIBLE_CONTENT_TYPE_REGEX.test(mimeType))) {
				const acceptEncodingSet = new Set(c.req.header("Accept-Encoding")?.split(",").map((encoding) => encoding.trim()));
				for (const encoding of ENCODINGS_ORDERED_KEYS) {
					if (!acceptEncodingSet.has(encoding)) continue;
					const compressedContent = await getContent(path + ENCODINGS[encoding], c);
					if (compressedContent) {
						content = compressedContent;
						c.header("Content-Encoding", encoding);
						c.header("Vary", "Accept-Encoding", { append: true });
						break;
					}
				}
			}
			await options.onFound?.(path, c);
			return c.body(content);
		}
		await options.onNotFound?.(path, c);
		await next();
	};
};
//#endregion
exports.serveStatic = serveStatic;
