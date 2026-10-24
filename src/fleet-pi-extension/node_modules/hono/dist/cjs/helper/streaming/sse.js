Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_html = require("../../utils/html.js");
const require_utils_stream = require("../../utils/stream.js");
const require_helper_streaming_utils = require("./utils.js");
//#region src/helper/streaming/sse.ts
var SSEStreamingApi = class extends require_utils_stream.StreamingApi {
	constructor(writable, readable) {
		super(writable, readable);
	}
	async writeSSE(message) {
		const dataLines = (await require_utils_html.resolveCallback(message.data, require_utils_html.HtmlEscapedCallbackPhase.Stringify, false, {})).split(/\r\n|\r|\n/).map((line) => {
			return `data: ${line}`;
		}).join("\n");
		for (const key of ["event", "id"]) {
			const value = message[key];
			if (value && /[\r\n]/.test(value)) throw new Error(`${key} must not contain "\\r" or "\\n"`);
		}
		const sseData = [
			message.event && `event: ${message.event}`,
			dataLines,
			message.id !== void 0 && `id: ${message.id}`,
			message.retry !== void 0 && `retry: ${message.retry}`
		].filter(Boolean).join("\n") + "\n\n";
		await this.write(sseData);
	}
};
const run = async (stream, cb, onError) => {
	try {
		await cb(stream);
	} catch (e) {
		if (e instanceof Error && onError) {
			await onError(e, stream);
			await stream.writeSSE({
				event: "error",
				data: e.message
			});
		} else console.error(e);
	} finally {
		stream.close();
	}
};
const contextStash = /* @__PURE__ */ new WeakMap();
const streamSSE = (c, cb, onError) => {
	const { readable, writable } = new TransformStream();
	const stream = new SSEStreamingApi(writable, readable);
	if (require_helper_streaming_utils.isOldBunVersion()) c.req.raw.signal.addEventListener("abort", () => {
		if (!stream.closed) stream.abort();
	});
	contextStash.set(stream.responseReadable, c);
	c.header("Transfer-Encoding", "chunked");
	c.header("Content-Type", "text/event-stream");
	c.header("Cache-Control", "no-cache");
	c.header("Connection", "keep-alive");
	run(stream, cb, onError);
	return c.newResponse(stream.responseReadable);
};
//#endregion
exports.SSEStreamingApi = SSEStreamingApi;
exports.streamSSE = streamSSE;
