Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_html = require("../../utils/html.js");
//#region src/helper/html/index.ts
/**
* @module
* html Helper for Hono.
*/
const html = (strings, ...values) => {
	const buffer = [""];
	for (let i = 0, len = strings.length - 1; i < len; i++) {
		buffer[0] += strings[i];
		const children = Array.isArray(values[i]) ? values[i].flat(Infinity) : [values[i]];
		for (let i = 0, len = children.length; i < len; i++) {
			const child = children[i];
			if (typeof child === "string") require_utils_html.escapeToBuffer(child, buffer);
			else if (typeof child === "number") buffer[0] += child;
			else if (typeof child === "boolean" || child === null || child === void 0) continue;
			else if (typeof child === "object" && child.isEscaped) {
				if (child.callbacks) buffer.unshift("", child);
				else {
					const tmp = child.toString();
					if (tmp instanceof Promise) buffer.unshift("", tmp);
					else buffer[0] += tmp;
				}
			} else if (child instanceof Promise) buffer.unshift("", child);
			else require_utils_html.escapeToBuffer(child.toString(), buffer);
		}
	}
	buffer[0] += strings.at(-1);
	return buffer.length === 1 ? "callbacks" in buffer ? require_utils_html.raw(require_utils_html.resolveCallbackSync(require_utils_html.raw(buffer[0], buffer.callbacks))) : require_utils_html.raw(buffer[0]) : require_utils_html.stringBufferToString(buffer, buffer.callbacks);
};
//#endregion
exports.html = html;
exports.raw = require_utils_html.raw;
