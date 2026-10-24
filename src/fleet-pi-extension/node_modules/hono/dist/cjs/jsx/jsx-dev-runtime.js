Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_jsx_base = require("./base.js");
//#region src/jsx/jsx-dev-runtime.ts
function jsxDEV(tag, props, key) {
	let node;
	if (!props || !("children" in props)) node = require_jsx_base.jsxFn(tag, props, []);
	else {
		const children = props.children;
		node = Array.isArray(children) ? require_jsx_base.jsxFn(tag, props, children) : require_jsx_base.jsxFn(tag, props, [children]);
	}
	node.key = key;
	return node;
}
//#endregion
exports.Fragment = require_jsx_base.Fragment;
exports.jsxDEV = jsxDEV;
