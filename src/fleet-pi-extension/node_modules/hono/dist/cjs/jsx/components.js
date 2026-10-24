Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_html = require("../utils/html.js");
const require_jsx_constants = require("./constants.js");
const require_jsx_context = require("./context.js");
const require_jsx_base = require("./base.js");
const require_jsx_dom_components = require("./dom/components.js");
const require_jsx_streaming = require("./streaming.js");
//#region src/jsx/components.ts
let errorBoundaryCounter = 0;
const childrenToString = async (children) => {
	try {
		return children.flat().map(resolveChildEarly);
	} catch (e) {
		if (e instanceof Promise) {
			const resume = require_jsx_context.captureRenderContext();
			await e;
			return resume(() => childrenToString(children));
		} else throw e;
	}
};
const resolveChildEarly = (child) => {
	if (child == null || typeof child === "boolean") return "";
	else if (typeof child === "string" || Array.isArray(child)) return require_jsx_base.renderChildren([child]);
	else if (require_jsx_base.isUntrustedObject(child)) return require_jsx_base.renderUntrustedObject(child);
	else {
		const str = child.toString();
		return str instanceof Promise ? str : require_utils_html.raw(str);
	}
};
/**
* @experimental
* `ErrorBoundary` is an experimental feature.
* The API might be changed.
*/
const ErrorBoundary = async ({ children, fallback, fallbackRender, onError }) => {
	if (!children) return require_utils_html.raw("");
	if (!Array.isArray(children)) children = [children];
	const nonce = require_jsx_context.useContext(require_jsx_streaming.StreamingContext)?.scriptNonce;
	let resume;
	const getResume = () => resume ||= require_jsx_context.captureRenderContext();
	let fallbackStrPromise;
	const resolveFallbackStr = () => fallbackStrPromise ||= (async () => {
		const awaitedFallback = await fallback;
		if (awaitedFallback === null || awaitedFallback === void 0) return;
		if (typeof awaitedFallback === "string" || Array.isArray(awaitedFallback)) return getResume()(() => require_jsx_base.renderChildren([awaitedFallback]));
		if (require_jsx_base.isUntrustedObject(awaitedFallback)) return getResume()(() => require_jsx_base.renderUntrustedObject(awaitedFallback));
		const fallbackResult = await getResume()(() => awaitedFallback.toString());
		return require_utils_html.raw(fallbackResult, fallbackResult.callbacks || awaitedFallback.callbacks);
	})();
	const renderFallback = async (error) => {
		const fallbackStr = await resolveFallbackStr();
		return getResume()(async () => {
			onError?.(error);
			const fallbackRes = fallbackStr !== void 0 ? fallbackStr : fallbackRender && require_jsx_base.jsx(require_jsx_base.Fragment, {}, fallbackRender(error)) || "";
			const fallbackResString = await require_jsx_base.Fragment({ children: fallbackRes }).toString();
			return require_utils_html.raw(fallbackResString, fallbackResString.callbacks || fallbackRes.callbacks);
		});
	};
	let resArray = [];
	try {
		resArray = children.map(resolveChildEarly);
	} catch (e) {
		const resume = getResume();
		if (e instanceof Promise) resArray = [e.then(() => resume(() => childrenToString(children))).catch((e) => renderFallback(e))];
		else resArray = [await renderFallback(e)];
	}
	if (resArray.some((res) => res instanceof Promise)) {
		getResume();
		const index = errorBoundaryCounter++;
		const replaceRe = RegExp(`(<template id="E:${index}"></template>)(.*?)(<!--E:${index}-->)`, "s");
		let caught = false;
		const catchCallback = async ({ error, buffer }) => {
			if (caught) return "";
			caught = true;
			const fallbackResString = await renderFallback(error);
			const fallbackCallbacks = fallbackResString.callbacks;
			if (buffer) {
				buffer[0] = buffer[0].replace(replaceRe, () => fallbackResString);
				return fallbackCallbacks?.length ? require_utils_html.raw("", fallbackCallbacks) : "";
			}
			return require_utils_html.raw(`<template data-hono-target="E:${index}">${fallbackResString}</template><script>
((d,c,n) => {
c=d.currentScript.previousSibling
d=d.getElementById('E:${index}')
if(!d)return
do{n=d.nextSibling;n.remove()}while(n.nodeType!=8||n.nodeValue!='E:${index}')
d.replaceWith(c.content)
})(document)
<\/script>`, fallbackCallbacks);
		};
		let error;
		const promiseAll = Promise.all(resArray).catch((e) => error = e);
		return require_utils_html.raw(`<template id="E:${index}"></template><!--E:${index}-->`, [({ phase, buffer, context }) => {
			if (phase === require_utils_html.HtmlEscapedCallbackPhase.BeforeStream) return;
			return promiseAll.then(async (htmlArray) => {
				if (error) throw error;
				htmlArray = htmlArray.flat();
				const content = htmlArray.join("");
				let html = buffer ? "" : `<template data-hono-target="E:${index}">${content}</template><script${nonce ? ` nonce="${nonce}"` : ""}>
((d,c) => {
c=d.currentScript.previousSibling
d=d.getElementById('E:${index}')
if(!d)return
d.parentElement.insertBefore(c.content,d.nextSibling)
})(document)
<\/script>`;
				if (htmlArray.every((html) => !html.callbacks?.length)) {
					if (buffer) buffer[0] = buffer[0].replace(replaceRe, () => content);
					return html;
				}
				if (buffer) buffer[0] = buffer[0].replace(replaceRe, (_all, pre, _, post) => `${pre}${content}${post}`);
				const callbacks = htmlArray.map((html) => html.callbacks || []).flat();
				if (phase === require_utils_html.HtmlEscapedCallbackPhase.Stream) html = await require_utils_html.resolveCallback(html, require_utils_html.HtmlEscapedCallbackPhase.BeforeStream, true, context);
				let resolvedCount = 0;
				const promises = callbacks.map((c) => (...args) => c(...args)?.then((content) => {
					resolvedCount++;
					if (buffer) {
						if (resolvedCount === callbacks.length) buffer[0] = buffer[0].replace(replaceRe, (_all, _pre, content) => content);
						buffer[0] += content;
						return require_utils_html.raw("", content.callbacks);
					}
					return require_utils_html.raw(content + (resolvedCount !== callbacks.length ? "" : `<script>
((d,c,n) => {
d=d.getElementById('E:${index}')
if(!d)return
n=d.nextSibling
while(n.nodeType!=8||n.nodeValue!='E:${index}'){n=n.nextSibling}
n.remove()
d.remove()
})(document)
<\/script>`), content.callbacks);
				}).catch((error) => catchCallback({
					error,
					buffer
				})));
				return require_utils_html.raw(html, promises);
			}).catch((error) => catchCallback({
				error,
				buffer
			}));
		}]);
	} else return require_jsx_base.Fragment({ children: resArray });
};
ErrorBoundary[require_jsx_constants.DOM_RENDERER] = require_jsx_dom_components.ErrorBoundary;
//#endregion
exports.ErrorBoundary = ErrorBoundary;
exports.childrenToString = childrenToString;
