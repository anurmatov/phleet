import { escapeToBuffer, raw } from "../../utils/html.js";
import { DOM_RENDERER } from "../../jsx/constants.js";
import { CLASS_NAME, DEFAULT_STYLE_ID, SELECTOR, SELECTORS, STYLE_STRING, cssCommon, cxCommon, keyframesCommon, rawCssString, viewTransitionCommon } from "./common.js";
import { createCssJsxDomObjects } from "../../jsx/dom/css.js";
//#region src/helper/css/index.ts
/**
* @module
* css Helper for Hono.
*/
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
	const [cssJsxDomObject, StyleRenderToDom] = createCssJsxDomObjects({ id });
	const contextMap = /* @__PURE__ */ new WeakMap();
	const nonceMap = /* @__PURE__ */ new WeakMap();
	const replaceStyleRe = new RegExp(`(<style id="${id}"(?: nonce="[^"]*")?>.*?)(</style>)`);
	const newCssClassNameObject = (cssClassName) => {
		const appendStyle = ({ buffer, context }) => {
			const [toAdd, added] = contextMap.get(context);
			const names = Object.keys(toAdd);
			if (!names.length) return;
			let stylesStr = "";
			names.forEach((className) => {
				added[className] = true;
				stylesStr += className.startsWith(":-hono-global") ? toAdd[className] : `${className[0] === "@" ? "" : "."}${className}{${toAdd[className]}}`;
			});
			contextMap.set(context, [{}, added]);
			if (buffer && replaceStyleRe.test(buffer[0])) {
				buffer[0] = buffer[0].replace(replaceStyleRe, (_, pre, post) => `${pre}${stylesStr}${post}`);
				return;
			}
			const nonce = nonceMap.get(context);
			const appendStyleScript = `<script${nonce ? ` nonce="${nonce}"` : ""}>document.querySelector('#${id}').textContent+=${JSON.stringify(stylesStr)}<\/script>`;
			if (buffer) {
				buffer[0] = `${appendStyleScript}${buffer[0]}`;
				return;
			}
			return Promise.resolve(appendStyleScript);
		};
		const addClassNameToContext = ({ context }) => {
			if (!contextMap.has(context)) contextMap.set(context, [{}, {}]);
			const [toAdd, added] = contextMap.get(context);
			let allAdded = true;
			if (!added[cssClassName[SELECTOR]]) {
				allAdded = false;
				toAdd[cssClassName[SELECTOR]] = cssClassName[STYLE_STRING];
			}
			cssClassName[SELECTORS].forEach(({ [CLASS_NAME]: className, [STYLE_STRING]: styleString }) => {
				if (!added[className]) {
					allAdded = false;
					toAdd[className] = styleString;
				}
			});
			if (allAdded) return;
			return Promise.resolve(raw("", [appendStyle]));
		};
		const rawClassName = cssClassName[CLASS_NAME];
		let escapedClassName = rawClassName;
		if (/[&<>'"]/.test(rawClassName)) {
			const escapedBuffer = [""];
			escapeToBuffer(rawClassName, escapedBuffer);
			escapedClassName = escapedBuffer[0];
		}
		const className = new String(escapedClassName);
		Object.assign(className, cssClassName);
		className.isEscaped = true;
		className.callbacks = [addClassNameToContext];
		const promise = Promise.resolve(className);
		Object.assign(promise, cssClassName);
		promise.toString = cssJsxDomObject.toString;
		return promise;
	};
	const css = (strings, ...values) => {
		return newCssClassNameObject(cssCommon(strings, values, classNameSlug, onInvalidSlug));
	};
	const cx = (...args) => {
		args = cxCommon(args);
		return css(Array(args.length).fill(""), ...args);
	};
	const keyframes = (strings, ...values) => keyframesCommon(strings, values, classNameSlug, onInvalidSlug);
	const viewTransition = ((strings, ...values) => {
		return newCssClassNameObject(viewTransitionCommon(strings, values, classNameSlug, onInvalidSlug));
	});
	const Style = ({ children, nonce } = {}) => raw(`<style id="${id}"${nonce ? ` nonce="${nonce}"` : ""}>${children ? children[STYLE_STRING] : ""}</style>`, [({ context }) => {
		nonceMap.set(context, nonce);
	}]);
	Style[DOM_RENDERER] = StyleRenderToDom;
	return {
		css,
		cx,
		keyframes,
		viewTransition,
		Style
	};
};
const defaultContext = createCssContext({ id: DEFAULT_STYLE_ID });
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
export { Style, createCssContext, css, cx, keyframes, rawCssString, viewTransition };
