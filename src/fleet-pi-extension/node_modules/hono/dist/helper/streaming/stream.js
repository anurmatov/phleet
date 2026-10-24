import { StreamingApi } from "../../utils/stream.js";
import { isOldBunVersion } from "./utils.js";
//#region src/helper/streaming/stream.ts
const contextStash = /* @__PURE__ */ new WeakMap();
const stream = (c, cb, onError) => {
	const { readable, writable } = new TransformStream();
	const stream = new StreamingApi(writable, readable);
	if (isOldBunVersion()) c.req.raw.signal.addEventListener("abort", () => {
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
export { stream };
