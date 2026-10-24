Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_stream = require("../../utils/stream.js");
const require_helper_streaming_utils = require("./utils.js");
//#region src/helper/streaming/stream.ts
const contextStash = /* @__PURE__ */ new WeakMap();
const stream = (c, cb, onError) => {
	const { readable, writable } = new TransformStream();
	const stream = new require_utils_stream.StreamingApi(writable, readable);
	if (require_helper_streaming_utils.isOldBunVersion()) c.req.raw.signal.addEventListener("abort", () => {
		if (!stream.closed) stream.abort();
	});
	contextStash.set(stream.responseReadable, c);
	(async () => {
		try {
			await cb(stream);
		} catch (e) {
			if (e === void 0) {} else if (e instanceof Error && onError) await onError(e, stream);
			else console.error(e);
		} finally {
			stream.close();
		}
	})();
	return c.newResponse(stream.responseReadable);
};
//#endregion
exports.stream = stream;
