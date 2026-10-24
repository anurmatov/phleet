Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/helper/css/common.ts
const PSEUDO_GLOBAL_SELECTOR = ":-hono-global";
const isPseudoGlobalSelectorRe = new RegExp(`^${PSEUDO_GLOBAL_SELECTOR}{(.*)}$`);
const DEFAULT_STYLE_ID = "hono-css";
const SELECTOR = Symbol();
const CLASS_NAME = Symbol();
const STYLE_STRING = Symbol();
const SELECTORS = Symbol();
const EXTERNAL_CLASS_NAMES = Symbol();
const CSS_ESCAPED = Symbol();
const IS_CSS_ESCAPED = Symbol();
/**
* @experimental
* `rawCssString` is an experimental feature.
* The API might be changed.
*/
const rawCssString = (value) => {
	return { [CSS_ESCAPED]: value };
};
/**
* Used the goober'code as a reference:
* https://github.com/cristianbote/goober/blob/master/src/core/to-hash.js
* MIT License, Copyright (c) 2019 Cristian Bote
*/
const toHash = (str) => {
	let i = 0, out = 11;
	while (i < str.length) out = 101 * out + str.charCodeAt(i++) >>> 0;
	return "css-" + out;
};
const normalizeLabel = (label) => {
	return label.trim().replace(/\s+/g, "-");
};
const isValidClassName = (name) => /^-?[_a-zA-Z][_a-zA-Z0-9-]*$/.test(name);
const hasUnsafeSelectorChar = (name) => /[<{}]/.test(name);
const RESERVED_KEYFRAME_NAMES = /* @__PURE__ */ new Set([
	"default",
	"inherit",
	"initial",
	"none",
	"revert",
	"revert-layer",
	"unset"
]);
const isValidKeyframeName = (name) => isValidClassName(name) && !RESERVED_KEYFRAME_NAMES.has(name.toLowerCase());
const defaultOnInvalidSlug = (slug) => {
	console.warn(`Invalid slug: ${slug}`);
};
const cssStringReStr = ["\"(?:(?:\\\\[\\s\\S]|[^\"\\\\])*)\"", "'(?:(?:\\\\[\\s\\S]|[^'\\\\])*)'"].join("|");
const minifyCssRe = new RegExp([
	"(" + cssStringReStr + ")",
	"(?:" + [
		"^\\s+",
		"\\/\\*.*?\\*\\/\\s*",
		"\\/\\/.*\\n\\s*",
		"\\s+$"
	].join("|") + ")",
	"\\s*;\\s*(}|$)\\s*",
	"\\s*([{};:,])\\s*",
	"(\\s)\\s+"
].join("|"), "g");
const minify = (css) => {
	return css.replace(minifyCssRe, (_, $1, $2, $3, $4) => $1 || $2 || $3 || $4 || "");
};
const buildStyleString = (strings, values) => {
	const selectors = [];
	const externalClassNames = [];
	const label = strings[0].match(/^\s*\/\*(.*?)\*\//)?.[1] || "";
	let styleString = "";
	for (let i = 0, len = strings.length; i < len; i++) {
		styleString += strings[i];
		let vArray = values[i];
		if (typeof vArray === "boolean" || vArray === null || vArray === void 0) continue;
		if (!Array.isArray(vArray)) vArray = [vArray];
		for (let j = 0, len = vArray.length; j < len; j++) {
			let value = vArray[j];
			if (typeof value === "boolean" || value === null || value === void 0) continue;
			if (typeof value === "string") {
				if (/([\\"'\/])/.test(value)) styleString += value.replace(/([\\"']|(?<=<)\/)/g, "\\$1");
				else styleString += value;
			} else if (typeof value === "number") styleString += value;
			else if (value[CSS_ESCAPED]) styleString += value[CSS_ESCAPED];
			else if (value[CLASS_NAME].startsWith("@keyframes ")) {
				selectors.push(value);
				styleString += ` ${value[CLASS_NAME].substring(11)} `;
			} else {
				if (strings[i + 1]?.match(/^\s*{/)) {
					selectors.push(value);
					value = `.${value[CLASS_NAME]}`;
				} else {
					selectors.push(...value[SELECTORS]);
					externalClassNames.push(...value[EXTERNAL_CLASS_NAMES]);
					value = value[STYLE_STRING];
					const valueLen = value.length;
					if (valueLen > 0) {
						const lastChar = value[valueLen - 1];
						if (lastChar !== ";" && lastChar !== "}") value += ";";
					}
				}
				styleString += `${value || ""}`;
			}
		}
	}
	return [
		label,
		minify(styleString),
		selectors,
		externalClassNames
	];
};
const cssCommon = (strings, values, classNameSlug, onInvalidSlug) => {
	let [label, thisStyleString, selectors, externalClassNames] = buildStyleString(strings, values);
	const isPseudoGlobal = isPseudoGlobalSelectorRe.exec(thisStyleString);
	if (isPseudoGlobal) thisStyleString = isPseudoGlobal[1];
	const hash = toHash(label + thisStyleString);
	let customSlug;
	if (classNameSlug) {
		const slug = classNameSlug(hash, normalizeLabel(label), thisStyleString);
		if (slug) {
			if (isValidClassName(slug)) customSlug = slug;
			else (onInvalidSlug || defaultOnInvalidSlug)(slug);
		}
	}
	const selector = (isPseudoGlobal ? PSEUDO_GLOBAL_SELECTOR : "") + (customSlug || hash);
	const className = (isPseudoGlobal ? selectors.map((s) => s[CLASS_NAME]) : [selector, ...externalClassNames]).join(" ");
	return {
		[SELECTOR]: selector,
		[CLASS_NAME]: className,
		[STYLE_STRING]: thisStyleString,
		[SELECTORS]: selectors,
		[EXTERNAL_CLASS_NAMES]: externalClassNames
	};
};
const cxCommon = (args) => {
	for (let i = 0, len = args.length; i < len; i++) {
		const arg = args[i];
		if (typeof arg === "string") args[i] = {
			[SELECTOR]: "",
			[CLASS_NAME]: "",
			[STYLE_STRING]: "",
			[SELECTORS]: [],
			[EXTERNAL_CLASS_NAMES]: hasUnsafeSelectorChar(arg) ? [] : [arg]
		};
	}
	return args;
};
const keyframesCommon = (strings, values, classNameSlug, onInvalidSlug) => {
	const [label, styleString] = buildStyleString(strings, values);
	const hash = toHash(label + styleString);
	let customSlug;
	if (classNameSlug) {
		const slug = classNameSlug(hash, normalizeLabel(label), styleString);
		if (slug) {
			if (isValidKeyframeName(slug)) customSlug = slug;
			else (onInvalidSlug || defaultOnInvalidSlug)(slug);
		}
	}
	return {
		[SELECTOR]: "",
		[CLASS_NAME]: `@keyframes ${customSlug || hash}`,
		[STYLE_STRING]: styleString,
		[SELECTORS]: [],
		[EXTERNAL_CLASS_NAMES]: []
	};
};
let viewTransitionNameIndex = 0;
const viewTransitionCommon = ((strings, values, classNameSlug, onInvalidSlug) => {
	if (!strings) strings = [`/* h-v-t ${viewTransitionNameIndex++} */`];
	const content = Array.isArray(strings) ? cssCommon(strings, values, classNameSlug, onInvalidSlug) : strings;
	const transitionName = content[CLASS_NAME];
	const res = cssCommon(["view-transition-name:", ""], [transitionName], classNameSlug, onInvalidSlug);
	content[CLASS_NAME] = PSEUDO_GLOBAL_SELECTOR + content[CLASS_NAME];
	content[STYLE_STRING] = content[STYLE_STRING].replace(/(?<=::view-transition(?:[a-z-]*)\()(?=\))/g, transitionName);
	res[CLASS_NAME] = res[SELECTOR] = transitionName;
	res[SELECTORS] = [...content[SELECTORS], content];
	return res;
});
//#endregion
exports.CLASS_NAME = CLASS_NAME;
exports.DEFAULT_STYLE_ID = DEFAULT_STYLE_ID;
exports.EXTERNAL_CLASS_NAMES = EXTERNAL_CLASS_NAMES;
exports.IS_CSS_ESCAPED = IS_CSS_ESCAPED;
exports.PSEUDO_GLOBAL_SELECTOR = PSEUDO_GLOBAL_SELECTOR;
exports.SELECTOR = SELECTOR;
exports.SELECTORS = SELECTORS;
exports.STYLE_STRING = STYLE_STRING;
exports.buildStyleString = buildStyleString;
exports.cssCommon = cssCommon;
exports.cxCommon = cxCommon;
exports.isPseudoGlobalSelectorRe = isPseudoGlobalSelectorRe;
exports.keyframesCommon = keyframesCommon;
exports.minify = minify;
exports.rawCssString = rawCssString;
exports.viewTransitionCommon = viewTransitionCommon;
