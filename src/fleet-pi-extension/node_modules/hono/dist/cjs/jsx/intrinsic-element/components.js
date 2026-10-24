Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_runtime = require("../../_virtual/_rolldown/runtime.js");
const require_utils_html = require("../../utils/html.js");
const require_jsx_constants = require("../constants.js");
const require_jsx_context = require("../context.js");
const require_jsx_intrinsic_element_common = require("./common.js");
const require_jsx_children = require("../children.js");
const require_jsx_base = require("../base.js");
//#region src/jsx/intrinsic-element/components.ts
var components_exports = /* @__PURE__ */ require_runtime.__exportAll({
	button: () => button,
	form: () => form,
	input: () => input,
	link: () => link,
	meta: () => meta,
	script: () => script,
	style: () => style,
	title: () => title
});
const metaTagMap = /* @__PURE__ */ new WeakMap();
const insertIntoHead = (tagName, tag, props, precedence) => ({ buffer, context }) => {
	if (!buffer) return;
	const map = metaTagMap.get(context) || {};
	metaTagMap.set(context, map);
	const tags = map[tagName] ||= [];
	let duped = false;
	const deDupeKeys = require_jsx_intrinsic_element_common.deDupeKeyMap[tagName];
	const deDupeByKey = require_jsx_intrinsic_element_common.shouldDeDupeByKey(tagName, precedence !== void 0);
	if (deDupeByKey) LOOP: for (const [, tagProps] of tags) {
		if (tagName === "link" && !(tagProps.rel === "stylesheet" && tagProps["data-precedence"] !== void 0)) continue;
		for (const key of deDupeKeys) if ((tagProps?.[key] ?? null) === props?.[key]) {
			duped = true;
			break LOOP;
		}
	}
	if (duped) buffer[0] = buffer[0].replaceAll(tag, "");
	else if (deDupeByKey || tagName === "link") tags.push([
		tag,
		props,
		precedence
	]);
	else tags.unshift([
		tag,
		props,
		precedence
	]);
	if (buffer[0].indexOf("</head>") !== -1) {
		let insertTags;
		if (tagName === "link" || precedence !== void 0) {
			const precedences = [];
			insertTags = tags.map(([tag, , tagPrecedence], index) => {
				if (tagPrecedence === void 0) return [
					tag,
					Number.MAX_SAFE_INTEGER,
					index
				];
				let order = precedences.indexOf(tagPrecedence);
				if (order === -1) {
					precedences.push(tagPrecedence);
					order = precedences.length - 1;
				}
				return [
					tag,
					order,
					index
				];
			}).sort((a, b) => a[1] - b[1] || a[2] - b[2]).map(([tag]) => tag);
		} else insertTags = tags.map(([tag]) => tag);
		insertTags.forEach((tag) => {
			buffer[0] = buffer[0].replaceAll(tag, "");
		});
		buffer[0] = buffer[0].replace(/(?=<\/head>)/, () => insertTags.join(""));
	}
};
const returnWithoutSpecialBehavior = (tag, children, props) => require_jsx_base.renderChildren([new require_jsx_base.JSXNode(tag, props, require_jsx_children.toArray(children ?? []))]);
const documentMetadataTag = (tag, children, props, sort) => {
	if ("itemProp" in props) return returnWithoutSpecialBehavior(tag, children, props);
	let { precedence, blocking, ...restProps } = props;
	precedence = sort ? precedence ?? "" : void 0;
	if (sort) restProps[require_jsx_intrinsic_element_common.dataPrecedenceAttr] = precedence;
	const string = new require_jsx_base.JSXNode(tag, restProps, require_jsx_children.toArray(children || [])).toString();
	if (string instanceof Promise) return string.then((resString) => require_utils_html.raw(resString, [...resString.callbacks || [], insertIntoHead(tag, resString, restProps, precedence)]));
	else return require_utils_html.raw(string, [insertIntoHead(tag, string, restProps, precedence)]);
};
const title = ({ children, ...props }) => {
	const nameSpaceContext = require_jsx_base.getNameSpaceContext();
	if (nameSpaceContext) {
		const context = require_jsx_context.useContext(nameSpaceContext);
		if (context === "svg" || context === "head") return new require_jsx_base.JSXNode("title", props, require_jsx_children.toArray(children ?? []));
	}
	return documentMetadataTag("title", children, props, false);
};
const script = ({ children, ...props }) => {
	const nameSpaceContext = require_jsx_base.getNameSpaceContext();
	if (["src", "async"].some((k) => !props[k]) || nameSpaceContext && require_jsx_context.useContext(nameSpaceContext) === "head") return returnWithoutSpecialBehavior("script", children, props);
	return documentMetadataTag("script", children, props, false);
};
const style = ({ children, ...props }) => {
	if (!["href", "precedence"].every((k) => k in props)) return returnWithoutSpecialBehavior("style", children, props);
	props["data-href"] = props.href;
	delete props.href;
	return documentMetadataTag("style", children, props, true);
};
const link = ({ children, ...props }) => {
	if (["onLoad", "onError"].some((k) => k in props) || props.rel === "stylesheet" && (!("precedence" in props) || "disabled" in props)) return returnWithoutSpecialBehavior("link", children, props);
	return documentMetadataTag("link", children, props, require_jsx_intrinsic_element_common.isStylesheetLinkWithPrecedence(props));
};
const meta = ({ children, ...props }) => {
	const nameSpaceContext = require_jsx_base.getNameSpaceContext();
	if (nameSpaceContext && require_jsx_context.useContext(nameSpaceContext) === "head") return returnWithoutSpecialBehavior("meta", children, props);
	return documentMetadataTag("meta", children, props, false);
};
const newJSXNode = (tag, { children, ...props }) => new require_jsx_base.JSXNode(tag, props, require_jsx_children.toArray(children ?? []));
const form = (props) => {
	if (typeof props.action === "function") props.action = require_jsx_constants.PERMALINK in props.action ? props.action[require_jsx_constants.PERMALINK] : void 0;
	return newJSXNode("form", props);
};
const formActionableElement = (tag, props) => {
	if (typeof props.formAction === "function") props.formAction = require_jsx_constants.PERMALINK in props.formAction ? props.formAction[require_jsx_constants.PERMALINK] : void 0;
	return newJSXNode(tag, props);
};
const input = (props) => formActionableElement("input", props);
const button = (props) => formActionableElement("button", props);
//#endregion
exports.button = button;
Object.defineProperty(exports, "components_exports", {
	enumerable: true,
	get: function() {
		return components_exports;
	}
});
exports.form = form;
exports.input = input;
exports.link = link;
exports.meta = meta;
exports.script = script;
exports.style = style;
exports.title = title;
