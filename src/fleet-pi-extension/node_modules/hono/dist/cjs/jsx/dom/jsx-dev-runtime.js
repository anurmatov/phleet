Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_jsx_dom_intrinsic_element_components = require("./intrinsic-element/components.js");
//#region src/jsx/dom/jsx-dev-runtime.ts
const jsxDEV = (tag, props, key) => {
	if (typeof tag === "string" && require_jsx_dom_intrinsic_element_components.components_exports[tag]) tag = require_jsx_dom_intrinsic_element_components.components_exports[tag];
	return {
		tag,
		type: tag,
		props,
		key,
		ref: props.ref
	};
};
const Fragment = (props) => jsxDEV("", props, void 0);
//#endregion
exports.Fragment = Fragment;
exports.jsxDEV = jsxDEV;
