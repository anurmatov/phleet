Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_runtime = require("../../../_virtual/_rolldown/runtime.js");
const require_jsx_context = require("../../context.js");
const require_jsx_intrinsic_element_common = require("../../intrinsic-element/common.js");
const require_jsx_dom_render = require("../render.js");
const require_jsx_hooks_index = require("../../hooks/index.js");
const require_jsx_dom_hooks_index = require("../hooks/index.js");
//#region src/jsx/dom/intrinsic-element/components.ts
var components_exports = /* @__PURE__ */ require_runtime.__exportAll({
	button: () => button,
	clearCache: () => clearCache,
	composeRef: () => composeRef,
	form: () => form,
	input: () => input,
	link: () => link,
	meta: () => meta,
	script: () => script,
	style: () => style,
	title: () => title
});
const clearCache = () => {
	blockingPromiseMap = Object.create(null);
	createdElements = Object.create(null);
};
const composeRef = (ref, cb) => {
	return require_jsx_hooks_index.useMemo(() => (e) => {
		let refCleanup;
		if (ref) {
			if (typeof ref === "function") refCleanup = ref(e) || (() => {
				ref(null);
			});
			else if (ref && "current" in ref) {
				ref.current = e;
				refCleanup = () => {
					ref.current = null;
				};
			}
		}
		const cbCleanup = cb(e);
		return () => {
			cbCleanup?.();
			refCleanup?.();
		};
	}, [ref]);
};
let blockingPromiseMap = Object.create(null);
let createdElements = Object.create(null);
const documentMetadataTag = (tag, props, preserveNodeType, supportSort, supportBlocking) => {
	if (props?.itemProp) return {
		tag,
		props,
		type: tag,
		ref: props.ref
	};
	const head = document.head;
	let { onLoad, onError, precedence, blocking, ...restProps } = props;
	let element = null;
	let created = false;
	const deDupeKeys = require_jsx_intrinsic_element_common.deDupeKeyMap[tag];
	const deDupeByKey = require_jsx_intrinsic_element_common.shouldDeDupeByKey(tag, supportSort);
	const isDeDupeCandidateLink = (e) => e.getAttribute("rel") === "stylesheet" && e.getAttribute("data-precedence") !== null;
	let existingElements = void 0;
	if (deDupeByKey) {
		const tags = head.querySelectorAll(tag);
		LOOP: for (const e of tags) {
			if (tag === "link" && !isDeDupeCandidateLink(e)) continue;
			for (const key of deDupeKeys) if (e.getAttribute(key) === props[key]) {
				element = e;
				break LOOP;
			}
		}
		if (!element) {
			const cacheKey = deDupeKeys.reduce((acc, key) => props[key] === void 0 ? acc : `${acc}-${key}-${props[key]}`, tag);
			created = !createdElements[cacheKey];
			element = createdElements[cacheKey] ||= (() => {
				const e = document.createElement(tag);
				for (const key of deDupeKeys) if (props[key] !== void 0) e.setAttribute(key, props[key]);
				if (props.rel) e.setAttribute("rel", props.rel);
				return e;
			})();
		}
	} else existingElements = head.querySelectorAll(tag);
	precedence = supportSort ? precedence ?? "" : void 0;
	if (supportSort) restProps[require_jsx_intrinsic_element_common.dataPrecedenceAttr] = precedence;
	const insert = require_jsx_hooks_index.useCallback((e) => {
		if (deDupeByKey) {
			if (tag === "link" && precedence !== void 0) {
				let found = false;
				for (const existingElement of head.querySelectorAll(tag)) {
					const existingPrecedence = existingElement.getAttribute(require_jsx_intrinsic_element_common.dataPrecedenceAttr);
					if (existingPrecedence === null) {
						head.insertBefore(e, existingElement);
						return;
					}
					if (found && existingPrecedence !== precedence) {
						head.insertBefore(e, existingElement);
						return;
					}
					if (existingPrecedence === precedence) found = true;
				}
				head.appendChild(e);
				return;
			}
			let found = false;
			for (const existingElement of head.querySelectorAll(tag)) {
				if (found && existingElement.getAttribute("data-precedence") !== precedence) {
					head.insertBefore(e, existingElement);
					return;
				}
				if (existingElement.getAttribute("data-precedence") === precedence) found = true;
			}
			head.appendChild(e);
		} else if (tag === "link") {
			if (!head.contains(e)) head.appendChild(e);
		} else if (existingElements) {
			let found = false;
			for (const existingElement of existingElements) if (existingElement === e) {
				found = true;
				break;
			}
			if (!found) head.insertBefore(e, head.contains(existingElements[0]) ? existingElements[0] : head.querySelector(tag));
			existingElements = void 0;
		}
	}, [
		deDupeByKey,
		precedence,
		tag
	]);
	const ref = composeRef(props.ref, (e) => {
		const key = deDupeKeys[0];
		if (preserveNodeType === 2) e.innerHTML = "";
		if (created || existingElements) insert(e);
		if (!onError && !onLoad) return;
		if (!key) return;
		let promise = blockingPromiseMap[e.getAttribute(key)] ||= new Promise((resolve, reject) => {
			e.addEventListener("load", resolve);
			e.addEventListener("error", reject);
		});
		if (onLoad) promise = promise.then(onLoad);
		if (onError) promise = promise.catch(onError);
		promise.catch(() => {});
	});
	if (supportBlocking && blocking === "render") {
		const key = require_jsx_intrinsic_element_common.deDupeKeyMap[tag][0];
		if (key && props[key]) {
			const value = props[key];
			const promise = blockingPromiseMap[value] ||= new Promise((resolve, reject) => {
				insert(element);
				element.addEventListener("load", resolve);
				element.addEventListener("error", reject);
			});
			require_jsx_hooks_index.use(promise);
		}
	}
	const jsxNode = {
		tag,
		type: tag,
		props: {
			...restProps,
			ref
		},
		ref
	};
	jsxNode.p = preserveNodeType;
	if (element) jsxNode.e = element;
	return require_jsx_dom_render.createPortal(jsxNode, head);
};
const title = (props) => {
	const nameSpaceContext = require_jsx_dom_render.getNameSpaceContext();
	if ((nameSpaceContext && require_jsx_context.useContext(nameSpaceContext))?.endsWith("svg")) return {
		tag: "title",
		props,
		type: "title",
		ref: props.ref
	};
	return documentMetadataTag("title", props, void 0, false, false);
};
const script = (props) => {
	if (!props || ["src", "async"].some((k) => !props[k])) return {
		tag: "script",
		props,
		type: "script",
		ref: props.ref
	};
	return documentMetadataTag("script", props, 1, false, true);
};
const style = (props) => {
	if (!props || !["href", "precedence"].every((k) => k in props)) return {
		tag: "style",
		props,
		type: "style",
		ref: props.ref
	};
	props["data-href"] = props.href;
	delete props.href;
	return documentMetadataTag("style", props, 2, true, true);
};
const link = (props) => {
	if (!props || ["onLoad", "onError"].some((k) => k in props) || props.rel === "stylesheet" && (!("precedence" in props) || "disabled" in props)) return {
		tag: "link",
		props,
		type: "link",
		ref: props.ref
	};
	return documentMetadataTag("link", props, 1, require_jsx_intrinsic_element_common.isStylesheetLinkWithPrecedence(props), true);
};
const meta = (props) => {
	return documentMetadataTag("meta", props, void 0, false, false);
};
const customEventFormAction = Symbol();
const form = (props) => {
	const { action, ...restProps } = props;
	if (typeof action !== "function") restProps.action = action;
	const [state, setState] = require_jsx_hooks_index.useState([null, false]);
	const onSubmit = require_jsx_hooks_index.useCallback(async (ev) => {
		const currentAction = ev.isTrusted ? action : ev.detail[customEventFormAction];
		if (typeof currentAction !== "function") return;
		ev.preventDefault();
		const formData = new FormData(ev.target);
		setState([formData, true]);
		const actionRes = currentAction(formData);
		if (actionRes instanceof Promise) {
			require_jsx_dom_hooks_index.registerAction(actionRes);
			await actionRes;
		}
		setState([null, true]);
	}, []);
	const ref = composeRef(props.ref, (el) => {
		el.addEventListener("submit", onSubmit);
		return () => {
			el.removeEventListener("submit", onSubmit);
		};
	});
	const [data, isDirty] = state;
	state[1] = false;
	return {
		tag: require_jsx_dom_hooks_index.FormContext,
		props: {
			value: {
				pending: data !== null,
				data,
				method: data ? "post" : null,
				action: data ? action : null
			},
			children: {
				tag: "form",
				props: {
					...restProps,
					ref
				},
				type: "form",
				ref
			}
		},
		f: isDirty
	};
};
const formActionableElement = (tag, { formAction, ...props }) => {
	if (typeof formAction === "function") {
		const onClick = require_jsx_hooks_index.useCallback((ev) => {
			ev.preventDefault();
			ev.currentTarget.form.dispatchEvent(new CustomEvent("submit", { detail: { [customEventFormAction]: formAction } }));
		}, []);
		props.ref = composeRef(props.ref, (el) => {
			el.addEventListener("click", onClick);
			return () => {
				el.removeEventListener("click", onClick);
			};
		});
	}
	return {
		tag,
		props,
		type: tag,
		ref: props.ref
	};
};
const input = (props) => formActionableElement("input", props);
const button = (props) => formActionableElement("button", props);
Object.assign(require_jsx_intrinsic_element_common.domRenderers, {
	title,
	script,
	style,
	link,
	meta,
	form,
	input,
	button
});
//#endregion
exports.button = button;
exports.clearCache = clearCache;
Object.defineProperty(exports, "components_exports", {
	enumerable: true,
	get: function() {
		return components_exports;
	}
});
exports.composeRef = composeRef;
exports.form = form;
exports.input = input;
exports.link = link;
exports.meta = meta;
exports.script = script;
exports.style = style;
exports.title = title;
