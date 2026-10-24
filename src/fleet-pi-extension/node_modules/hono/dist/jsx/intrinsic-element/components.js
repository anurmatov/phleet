import { __exportAll } from "../../_virtual/_rolldown/runtime.js";
import { raw } from "../../utils/html.js";
import { PERMALINK } from "../constants.js";
import { useContext } from "../context.js";
import { dataPrecedenceAttr, deDupeKeyMap, isStylesheetLinkWithPrecedence, shouldDeDupeByKey } from "./common.js";
import { toArray } from "../children.js";
import { JSXNode, getNameSpaceContext, renderChildren } from "../base.js";
//#region src/jsx/intrinsic-element/components.ts
var components_exports = /* @__PURE__ */ __exportAll({
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
	const deDupeKeys = deDupeKeyMap[tagName];
	const deDupeByKey = shouldDeDupeByKey(tagName, precedence !== void 0);
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
const returnWithoutSpecialBehavior = (tag, children, props) => renderChildren([new JSXNode(tag, props, toArray(children ?? []))]);
const documentMetadataTag = (tag, children, props, sort) => {
	if ("itemProp" in props) return returnWithoutSpecialBehavior(tag, children, props);
	let { precedence, blocking, ...restProps } = props;
	precedence = sort ? precedence ?? "" : void 0;
	if (sort) restProps[dataPrecedenceAttr] = precedence;
	const string = new JSXNode(tag, restProps, toArray(children || [])).toString();
	if (string instanceof Promise) return string.then((resString) => raw(resString, [...resString.callbacks || [], insertIntoHead(tag, resString, restProps, precedence)]));
	else return raw(string, [insertIntoHead(tag, string, restProps, precedence)]);
};
const title = ({ children, ...props }) => {
	const nameSpaceContext = getNameSpaceContext();
	if (nameSpaceContext) {
		const context = useContext(nameSpaceContext);
		if (context === "svg" || context === "head") return new JSXNode("title", props, toArray(children ?? []));
	}
	return documentMetadataTag("title", children, props, false);
};
const script = ({ children, ...props }) => {
	const nameSpaceContext = getNameSpaceContext();
	if (["src", "async"].some((k) => !props[k]) || nameSpaceContext && useContext(nameSpaceContext) === "head") return returnWithoutSpecialBehavior("script", children, props);
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
	return documentMetadataTag("link", children, props, isStylesheetLinkWithPrecedence(props));
};
const meta = ({ children, ...props }) => {
	const nameSpaceContext = getNameSpaceContext();
	if (nameSpaceContext && useContext(nameSpaceContext) === "head") return returnWithoutSpecialBehavior("meta", children, props);
	return documentMetadataTag("meta", children, props, false);
};
const newJSXNode = (tag, { children, ...props }) => new JSXNode(tag, props, toArray(children ?? []));
const form = (props) => {
	if (typeof props.action === "function") props.action = PERMALINK in props.action ? props.action[PERMALINK] : void 0;
	return newJSXNode("form", props);
};
const formActionableElement = (tag, props) => {
	if (typeof props.formAction === "function") props.formAction = PERMALINK in props.formAction ? props.formAction[PERMALINK] : void 0;
	return newJSXNode(tag, props);
};
const input = (props) => formActionableElement("input", props);
const button = (props) => formActionableElement("button", props);
//#endregion
export { button, components_exports, form, input, link, meta, script, style, title };
