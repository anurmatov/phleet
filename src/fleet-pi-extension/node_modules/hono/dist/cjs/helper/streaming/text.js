Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_context = require("../../context.js");
const require_helper_streaming_stream = require("./stream.js");
//#region src/helper/streaming/text.ts
const streamText = (c, cb, onError) => {
	c.header("Content-Type", require_context.TEXT_PLAIN);
	c.header("X-Content-Type-Options", "nosniff");
	c.header("Transfer-Encoding", "chunked");
	return require_helper_streaming_stream.stream(c, cb, onError);
};
//#endregion
exports.streamText = streamText;
