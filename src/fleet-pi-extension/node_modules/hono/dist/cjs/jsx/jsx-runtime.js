Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_html = require("../utils/html.js");
const require_helper_html_index = require("../helper/html/index.js");
const require_jsx_utils = require("./utils.js");
const require_jsx_base = require("./base.js");
const require_jsx_jsx_dev_runtime = require("./jsx-dev-runtime.js");
//#region src/jsx/jsx-runtime.ts
const jsxAttr = (key, v) => {
	if (!require_jsx_utils.isValidAttributeName(key)) return require_utils_html.raw("");
	const buffer = [`${key}="`];
	if (key === "style" && typeof v === "object") {
		let styleStr = "";
		require_jsx_utils.styleObjectForEach(v, (property, value) => {
			if (value != null) styleStr += `${styleStr ? ";" : ""}${property}:${value}`;
		});
		require_utils_html.escapeToBuffer(styleStr, buffer);
		buffer[0] += "\"";
	} else if (typeof v === "string") {
		require_utils_html.escapeToBuffer(v, buffer);
		buffer[0] += "\"";
	} else if (v === null || v === void 0) return require_utils_html.raw("");
	else if (typeof v === "number" || v.isEscaped) buffer[0] += `${v}"`;
	else if (v instanceof Promise) buffer.unshift("\"", v);
	else {
		require_utils_html.escapeToBuffer(v.toString(), buffer);
		buffer[0] += "\"";
	}
	return buffer.length === 1 ? require_utils_html.raw(buffer[0]) : require_utils_html.stringBufferToString(buffer, void 0);
};
const jsxEscape = (value) => value;
//#endregion
exports.Fragment = require_jsx_base.Fragment;
exports.jsx = require_jsx_jsx_dev_runtime.jsxDEV;
exports.jsxAttr = jsxAttr;
exports.jsxEscape = jsxEscape;
exports.jsxTemplate = require_helper_html_index.html;
exports.jsxs = require_jsx_jsx_dev_runtime.jsxDEV;
