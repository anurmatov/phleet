import { components_exports } from "./intrinsic-element/components.js";
//#region src/jsx/dom/jsx-dev-runtime.ts
const jsxDEV = (tag, props, key) => {
	if (typeof tag === "string" && components_exports[tag]) tag = components_exports[tag];
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
export { Fragment, jsxDEV };
