Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_helper_css_common = require("../../helper/css/common.js");
//#region src/jsx/dom/css.ts
const splitRule = (rule) => {
	const result = [];
	let startPos = 0;
	let depth = 0;
	for (let i = 0, len = rule.length; i < len; i++) {
		const char = rule[i];
		if (char === "'" || char === "\"") {
			const quote = char;
			i++;
			for (; i < len; i++) {
				if (rule[i] === "\\") {
					i++;
					continue;
				}
				if (rule[i] === quote) break;
			}
			continue;
		}
		if (char === "{") {
			depth++;
			continue;
		}
		if (char === "}") {
			depth--;
			if (depth === 0) {
				result.push(rule.slice(startPos, i + 1));
				startPos = i + 1;
			}
			continue;
		}
	}
	return result;
};
const createCssJsxDomObjects = ({ id }) => {
	let styleSheet = void 0;
	const findStyleSheet = () => {
		if (!styleSheet) {
			styleSheet = document.querySelector(`style#${id}`)?.sheet;
			if (styleSheet) styleSheet.addedStyles = /* @__PURE__ */ new Set();
		}
		return styleSheet ? [styleSheet, styleSheet.addedStyles] : [];
	};
	const insertRule = (className, styleString) => {
		const [sheet, addedStyles] = findStyleSheet();
		if (!sheet || !addedStyles) {
			Promise.resolve().then(() => {
				if (!findStyleSheet()[0]) throw new Error("style sheet not found");
				insertRule(className, styleString);
			});
			return;
		}
		if (!addedStyles.has(className)) {
			addedStyles.add(className);
			(className.startsWith(":-hono-global") ? splitRule(styleString) : [`${className[0] === "@" ? "" : "."}${className}{${styleString}}`]).forEach((rule) => {
				sheet.insertRule(rule, sheet.cssRules.length);
			});
		}
	};
	const cssObject = { toString() {
		const selector = this[require_helper_css_common.SELECTOR];
		insertRule(selector, this[require_helper_css_common.STYLE_STRING]);
		this[require_helper_css_common.SELECTORS].forEach(({ [require_helper_css_common.CLASS_NAME]: className, [require_helper_css_common.STYLE_STRING]: styleString }) => {
			insertRule(className, styleString);
		});
		return this[require_helper_css_common.CLASS_NAME];
	} };
	const Style = ({ children, nonce }) => ({
		tag: "style",
		props: {
			id,
			nonce,
			children: children && (Array.isArray(children) ? children : [children]).map((c) => c[require_helper_css_common.STYLE_STRING])
		}
	});
	return [cssObject, Style];
};
/**
* @experimental
* `createCssContext` is an experimental feature.
* The API might be changed.
*
* @param options.id - The ID for the style element
* @param options.classNameSlug - Optional function to customize generated CSS class names
* @param options.onInvalidSlug - Optional callback function called when an invalid slug is returned from ClassNameSlug
*/
const createCssContext = ({ id, classNameSlug, onInvalidSlug }) => {
	const [cssObject, Style] = createCssJsxDomObjects({ id });
	const newCssClassNameObject = (cssClassName) => {
		cssClassName.toString = cssObject.toString;
		return cssClassName;
	};
	const css = (strings, ...values) => {
		return newCssClassNameObject(require_helper_css_common.cssCommon(strings, values, classNameSlug, onInvalidSlug));
	};
	const cx = (...args) => {
		args = require_helper_css_common.cxCommon(args);
		return css(Array(args.length).fill(""), ...args);
	};
	const keyframes = (strings, ...values) => require_helper_css_common.keyframesCommon(strings, values, classNameSlug, onInvalidSlug);
	const viewTransition = ((strings, ...values) => {
		return newCssClassNameObject(require_helper_css_common.viewTransitionCommon(strings, values, classNameSlug, onInvalidSlug));
	});
	return {
		css,
		cx,
		keyframes,
		viewTransition,
		Style
	};
};
const defaultContext = createCssContext({ id: require_helper_css_common.DEFAULT_STYLE_ID });
/**
* @experimental
* `css` is an experimental feature.
* The API might be changed.
*/
const css = defaultContext.css;
/**
* @experimental
* `cx` is an experimental feature.
* The API might be changed.
*/
const cx = defaultContext.cx;
/**
* @experimental
* `keyframes` is an experimental feature.
* The API might be changed.
*/
const keyframes = defaultContext.keyframes;
/**
* @experimental
* `viewTransition` is an experimental feature.
* The API might be changed.
*/
const viewTransition = defaultContext.viewTransition;
/**
* @experimental
* `Style` is an experimental feature.
* The API might be changed.
*/
const Style = defaultContext.Style;
//#endregion
exports.Style = Style;
exports.createCssContext = createCssContext;
exports.createCssJsxDomObjects = createCssJsxDomObjects;
exports.css = css;
exports.cx = cx;
exports.keyframes = keyframes;
exports.rawCssString = require_helper_css_common.rawCssString;
exports.viewTransition = viewTransition;
