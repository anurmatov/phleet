Object.defineProperties(exports, {
	__esModule: { value: true },
	[Symbol.toStringTag]: { value: "Module" }
});
const require_jsx_dom_render = require("./render.js");
const require_jsx_hooks_index = require("../hooks/index.js");
//#region src/jsx/dom/client.ts
/**
* Create a root object for rendering
* @param element Render target
* @param options Options for createRoot (not supported yet)
* @returns Root object has `render` and `unmount` methods
*/
const createRoot = (element, options = {}) => {
	let setJsxNode = void 0;
	if (Object.keys(options).length > 0) console.warn("createRoot options are not supported yet");
	return {
		render(jsxNode) {
			if (setJsxNode === null) throw new Error("Cannot update an unmounted root");
			if (setJsxNode) setJsxNode(jsxNode);
			else require_jsx_dom_render.renderNode(require_jsx_dom_render.buildNode({
				tag: () => {
					const [_jsxNode, _setJsxNode] = require_jsx_hooks_index.useState(jsxNode);
					setJsxNode = _setJsxNode;
					return _jsxNode;
				},
				props: {}
			}), element);
		},
		unmount() {
			setJsxNode?.(null);
			setJsxNode = null;
		}
	};
};
/**
* Create a root object and hydrate app to the target element.
* In hono/jsx/dom, hydrate is equivalent to render.
* @param element Render target
* @param reactNode A JSXNode to render
* @param options Options for createRoot (not supported yet)
* @returns Root object has `render` and `unmount` methods
*/
const hydrateRoot = (element, reactNode, options = {}) => {
	const root = createRoot(element, options);
	root.render(reactNode);
	return root;
};
var client_default = {
	createRoot,
	hydrateRoot
};
//#endregion
exports.createRoot = createRoot;
exports.default = client_default;
exports.hydrateRoot = hydrateRoot;
