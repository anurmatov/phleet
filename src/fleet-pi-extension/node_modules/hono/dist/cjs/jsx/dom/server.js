Object.defineProperties(exports, {
	__esModule: { value: true },
	[Symbol.toStringTag]: { value: "Module" }
});
const require_jsx_base = require("../base.js");
const require_jsx_streaming = require("../streaming.js");
const require_jsx_dom_index = require("./index.js");
//#region src/jsx/dom/server.ts
const prepareRoot = (element) => typeof element === "string" || Array.isArray(element) ? require_jsx_base.renderChildren([element]) : element;
/**
* Render JSX element to string.
* @param element JSX element to render.
* @param options Options for rendering.
* @returns Rendered string.
*/
const renderToString = (element, options = {}) => {
	if (Object.keys(options).length > 0) console.warn("options are not supported yet");
	element = prepareRoot(element);
	const res = element instanceof Promise ? element : element?.toString() ?? "";
	if (typeof res !== "string") throw new Error("Async component is not supported in renderToString");
	return res;
};
/**
* Render JSX element to readable stream.
* @param element JSX element to render.
* @param options Options for rendering.
* @returns Rendered readable stream.
*/
const renderToReadableStream = async (element, options = {}) => {
	if (Object.keys(options).some((key) => key !== "onError")) console.warn("options are not supported yet, except onError");
	element = prepareRoot(element);
	if (!element || typeof element !== "object") element = element?.toString() ?? "";
	return require_jsx_streaming.renderToReadableStream(element, options.onError);
};
var server_default = {
	renderToString,
	renderToReadableStream,
	version: require_jsx_dom_index.default
};
//#endregion
exports.default = server_default;
exports.renderToReadableStream = renderToReadableStream;
exports.renderToString = renderToString;
exports.version = require_jsx_dom_index.default;
