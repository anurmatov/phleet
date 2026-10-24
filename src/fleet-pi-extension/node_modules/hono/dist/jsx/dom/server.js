import { renderChildren } from "../base.js";
import { renderToReadableStream as renderToReadableStream$1 } from "../streaming.js";
import dom_default from "./index.js";
//#region src/jsx/dom/server.ts
const prepareRoot = (element) => typeof element === "string" || Array.isArray(element) ? renderChildren([element]) : element;
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
	return renderToReadableStream$1(element, options.onError);
};
var server_default = {
	renderToString,
	renderToReadableStream,
	version: dom_default
};
//#endregion
export { server_default as default, renderToReadableStream, renderToString, dom_default as version };
