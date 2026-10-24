Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_html = require("../utils/html.js");
const require_jsx_constants = require("./constants.js");
const require_jsx_context = require("./context.js");
const require_jsx_intrinsic_element_common = require("./intrinsic-element/common.js");
const require_jsx_intrinsic_element_components = require("./intrinsic-element/components.js");
const require_jsx_utils = require("./utils.js");
//#region src/jsx/base.ts
let nameSpaceContext = void 0;
const getNameSpaceContext = () => nameSpaceContext;
const toSVGAttributeName = (key) => /[A-Z]/.test(key) && key.match(/^(?:al|basel|clip(?:Path|Rule)$|co|do|fill|fl|fo|gl|let|lig|i|marker[EMS]|o|pai|pointe|sh|st[or]|text[^L]|tr|u|ve|w)/) ? key.replace(/([A-Z])/g, "-$1").toLowerCase() : key;
const emptyTags = [
	"area",
	"base",
	"br",
	"col",
	"embed",
	"hr",
	"img",
	"input",
	"keygen",
	"link",
	"meta",
	"param",
	"source",
	"track",
	"wbr"
];
const booleanAttributes = [
	"allowfullscreen",
	"async",
	"autofocus",
	"autoplay",
	"checked",
	"controls",
	"default",
	"defer",
	"disabled",
	"download",
	"formnovalidate",
	"hidden",
	"inert",
	"ismap",
	"itemscope",
	"loop",
	"multiple",
	"muted",
	"nomodule",
	"novalidate",
	"open",
	"playsinline",
	"readonly",
	"required",
	"reversed",
	"selected"
];
const resolveFunctionComponentResult = (result, suspendedContext) => result.then((resolved) => {
	if (typeof resolved !== "string" && !Array.isArray(resolved) && !(resolved instanceof JSXNode)) return resolved;
	const children = Array.isArray(resolved) ? resolved : [resolved];
	const render = () => {
		const buffer = [""];
		childrenToStringToBuffer(children, buffer);
		return buffer.length === 1 ? require_utils_html.raw(buffer[0], buffer.callbacks) : require_utils_html.stringBufferToString(buffer, buffer.callbacks);
	};
	return suspendedContext ? suspendedContext(render) : require_jsx_context.runWithRenderContext(render);
});
const childrenToStringToBuffer = (children, buffer) => {
	for (let i = 0, len = children.length; i < len; i++) {
		const child = children[i];
		if (typeof child === "string") require_utils_html.escapeToBuffer(child, buffer);
		else if (typeof child === "boolean" || child === null || child === void 0) continue;
		else if (child instanceof JSXNode) child.toStringToBuffer(buffer);
		else if (typeof child === "number") buffer[0] += child;
		else if (child.isEscaped) {
			buffer[0] += child;
			const callbacks = child.callbacks;
			if (callbacks) {
				buffer.callbacks ||= [];
				buffer.callbacks.push(...callbacks);
			}
		} else if (child instanceof Promise) buffer.unshift("", child);
		else childrenToStringToBuffer(child, buffer);
	}
};
const renderChildren = (children) => require_jsx_context.runWithRenderContext(() => {
	const buffer = [""];
	childrenToStringToBuffer(children, buffer);
	return buffer.length === 1 ? require_utils_html.raw(buffer[0], buffer.callbacks) : require_utils_html.stringBufferToString(buffer, buffer.callbacks);
});
const isUntrustedObject = (value) => typeof value === "object" && value !== null && !Array.isArray(value) && !(value instanceof JSXNode) && !(value instanceof Promise) && !value.isEscaped && typeof value.toString === "function";
const renderUntrustedObject = (value) => {
	const stringified = value.toString();
	const escape = (result) => renderChildren([String(result)]);
	return stringified instanceof Promise ? stringified.then(escape) : escape(stringified);
};
var JSXNode = class {
	tag;
	props;
	key;
	children;
	isEscaped = true;
	constructor(tag, props, children) {
		if (typeof tag !== "function" && !require_jsx_utils.isValidTagName(tag)) throw new Error(`Invalid JSX tag name: ${tag}`);
		this.tag = tag;
		this.props = props;
		this.children = children;
	}
	get type() {
		return this.tag;
	}
	get ref() {
		return this.props.ref || null;
	}
	toString() {
		const render = () => {
			const buffer = [""];
			this.toStringToBuffer(buffer);
			return buffer.length === 1 ? "callbacks" in buffer ? require_utils_html.resolveCallbackSync(require_utils_html.raw(buffer[0], buffer.callbacks)).toString() : buffer[0] : require_utils_html.stringBufferToString(buffer, buffer.callbacks);
		};
		return require_jsx_context.runWithRenderContext(render);
	}
	toStringToBuffer(buffer) {
		const tag = this.tag;
		const props = this.props;
		let { children } = this;
		buffer[0] += `<${tag}`;
		const normalizeKey = tag === "svg" || nameSpaceContext && require_jsx_context.useContext(nameSpaceContext) === "svg" ? (key) => toSVGAttributeName(require_jsx_utils.normalizeIntrinsicElementKey(key)) : (key) => require_jsx_utils.normalizeIntrinsicElementKey(key);
		for (let [key, v] of Object.entries(props)) {
			key = normalizeKey(key);
			if (!require_jsx_utils.isValidAttributeName(key)) continue;
			if (key === "children") {} else if (key === "style" && typeof v === "object") {
				let styleStr = "";
				require_jsx_utils.styleObjectForEach(v, (property, value) => {
					if (value != null) styleStr += `${styleStr ? ";" : ""}${property}:${value}`;
				});
				buffer[0] += " style=\"";
				require_utils_html.escapeToBuffer(styleStr, buffer);
				buffer[0] += "\"";
			} else if (typeof v === "string") {
				buffer[0] += ` ${key}="`;
				require_utils_html.escapeToBuffer(v, buffer);
				buffer[0] += "\"";
			} else if (v === null || v === void 0) {} else if (typeof v === "number" || v.isEscaped) buffer[0] += ` ${key}="${v}"`;
			else if (typeof v === "boolean" && booleanAttributes.includes(key)) {
				if (v) buffer[0] += ` ${key}=""`;
			} else if (key === "dangerouslySetInnerHTML") {
				if (children.length > 0) throw new Error("Can only set one of `children` or `props.dangerouslySetInnerHTML`.");
				children = [require_utils_html.raw(v.__html)];
			} else if (v instanceof Promise) {
				buffer[0] += ` ${key}="`;
				buffer.unshift("\"", v);
			} else if (typeof v === "function") {
				if (!key.startsWith("on") && key !== "ref") throw new Error(`Invalid prop '${key}' of type 'function' supplied to '${tag}'.`);
			} else {
				buffer[0] += ` ${key}="`;
				require_utils_html.escapeToBuffer(v.toString(), buffer);
				buffer[0] += "\"";
			}
		}
		if (emptyTags.includes(tag) && children.length === 0) {
			buffer[0] += "/>";
			return;
		}
		buffer[0] += ">";
		childrenToStringToBuffer(children, buffer);
		buffer[0] += `</${tag}>`;
	}
};
var JSXFunctionNode = class extends JSXNode {
	toStringToBuffer(buffer) {
		const { children } = this;
		const props = { ...this.props };
		if (children.length) props.children = children.length === 1 ? children[0] : children;
		const res = this.tag.call(null, props);
		if (typeof res === "boolean" || res == null) return;
		else if (res instanceof Promise) {
			if (require_jsx_context.globalContexts.length === 0) buffer.unshift("", resolveFunctionComponentResult(res));
			else buffer.unshift("", resolveFunctionComponentResult(res, require_jsx_context.captureRenderContext()));
		} else if (res instanceof JSXNode) res.toStringToBuffer(buffer);
		else if (Array.isArray(res)) childrenToStringToBuffer(res, buffer);
		else if (typeof res === "number" || res.isEscaped) {
			buffer[0] += res;
			if (res.callbacks) {
				buffer.callbacks ||= [];
				buffer.callbacks.push(...res.callbacks);
			}
		} else require_utils_html.escapeToBuffer(res, buffer);
	}
};
var JSXFragmentNode = class extends JSXNode {
	toStringToBuffer(buffer) {
		childrenToStringToBuffer(this.children, buffer);
	}
};
const jsx = (tag, props, ...children) => {
	props ??= {};
	if (children.length) props.children = children.length === 1 ? children[0] : children;
	const key = props.key;
	delete props["key"];
	const node = jsxFn(tag, props, children);
	node.key = key;
	return node;
};
let initDomRenderer = false;
const jsxFn = (tag, props, children) => {
	if (!initDomRenderer) {
		for (const k in require_jsx_intrinsic_element_common.domRenderers) require_jsx_intrinsic_element_components.components_exports[k][require_jsx_constants.DOM_RENDERER] = require_jsx_intrinsic_element_common.domRenderers[k];
		initDomRenderer = true;
	}
	if (typeof tag === "function") return new JSXFunctionNode(tag, props, children);
	else if (require_jsx_intrinsic_element_components.components_exports[tag]) return new JSXFunctionNode(require_jsx_intrinsic_element_components.components_exports[tag], props, children);
	else if (tag === "svg" || tag === "head") {
		nameSpaceContext ||= require_jsx_context.createContext("");
		return new JSXNode(tag, props, [new JSXFunctionNode(nameSpaceContext, { value: tag }, children)]);
	} else return new JSXNode(tag, props, children);
};
const shallowEqual = (a, b) => {
	if (a === b) return true;
	const aKeys = Object.keys(a).sort();
	const bKeys = Object.keys(b).sort();
	if (aKeys.length !== bKeys.length) return false;
	for (let i = 0, len = aKeys.length; i < len; i++) if (aKeys[i] === "children" && bKeys[i] === "children" && !a.children?.length && !b.children?.length) continue;
	else if (a[aKeys[i]] !== b[aKeys[i]]) return false;
	return true;
};
const memo = (component, propsAreEqual = shallowEqual) => {
	const wrapper = ((props) => component(props));
	wrapper[require_jsx_constants.DOM_MEMO] = propsAreEqual;
	wrapper[require_jsx_constants.DOM_RENDERER] = component;
	return wrapper;
};
const Fragment = ({ children }) => {
	return new JSXFragmentNode("", { children }, Array.isArray(children) ? children : children ? [children] : []);
};
const isValidElement = (element) => {
	return !!(element && typeof element === "object" && "tag" in element && "props" in element);
};
const cloneElement = (element, props, ...children) => {
	let childrenToClone;
	if (children.length > 0) childrenToClone = children;
	else {
		const c = element.props.children;
		childrenToClone = Array.isArray(c) ? c : [c];
	}
	return jsx(element.tag, {
		...element.props,
		...props
	}, ...childrenToClone);
};
const reactAPICompatVersion = "19.0.0-hono-jsx";
//#endregion
exports.Fragment = Fragment;
exports.JSXFragmentNode = JSXFragmentNode;
exports.JSXNode = JSXNode;
exports.booleanAttributes = booleanAttributes;
exports.cloneElement = cloneElement;
exports.getNameSpaceContext = getNameSpaceContext;
exports.isUntrustedObject = isUntrustedObject;
exports.isValidElement = isValidElement;
exports.jsx = jsx;
exports.jsxFn = jsxFn;
exports.memo = memo;
exports.reactAPICompatVersion = reactAPICompatVersion;
exports.renderChildren = renderChildren;
exports.renderUntrustedObject = renderUntrustedObject;
exports.shallowEqual = shallowEqual;
