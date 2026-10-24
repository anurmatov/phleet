import { DOM_ERROR_HANDLER, DOM_INTERNAL_TAG, DOM_MEMO, DOM_RENDERER, DOM_STASH } from "../constants.js";
import { createContext } from "./context.js";
import { globalContexts, useContext } from "../context.js";
import { toArray } from "../children.js";
import { normalizeIntrinsicElementKey, styleObjectForEach } from "../utils.js";
import "../hooks/index.js";
//#region src/jsx/dom/render.ts
const HONO_PORTAL_ELEMENT = "_hp";
const eventAliasMap = {
	Change: "Input",
	DoubleClick: "DblClick"
};
const nameSpaceMap = {
	svg: "2000/svg",
	math: "1998/Math/MathML"
};
const buildDataStack = [];
const refCleanupMap = /* @__PURE__ */ new WeakMap();
let nameSpaceContext = void 0;
const getNameSpaceContext = () => nameSpaceContext;
const isNodeString = (node) => "t" in node;
const eventCache = { onClick: ["click", false] };
const getEventSpec = (key) => {
	if (!key.startsWith("on")) return;
	if (eventCache[key]) return eventCache[key];
	const match = key.match(/^on([A-Z][a-zA-Z]+?(?:PointerCapture)?)(Capture)?$/);
	if (match) {
		const [, eventName, capture] = match;
		return eventCache[key] = [(eventAliasMap[eventName] || eventName).toLowerCase(), !!capture];
	}
};
const toAttributeName = (element, key) => nameSpaceContext && element instanceof SVGElement && /[A-Z]/.test(key) && (key in element.style || key.match(/^(?:o|pai|str|u|ve)/)) ? key.replace(/([A-Z])/g, "-$1").toLowerCase() : key;
const normalizeFormValue = (value) => value === null || value === void 0 || value === false ? null : value;
const applySelectValue = (select, props) => {
	if (!("value" in props)) return;
	select.value = normalizeFormValue(props["value"]);
	if (!select.multiple && select.selectedIndex === -1) select.selectedIndex = 0;
};
const isIgnorableAttributeError = (error) => error instanceof DOMException && error.name === "InvalidCharacterError";
const applyProps = (container, attributes, oldAttributes) => {
	attributes ||= {};
	for (let key in attributes) {
		const value = attributes[key];
		if (key !== "children" && (!oldAttributes || oldAttributes[key] !== value)) {
			key = normalizeIntrinsicElementKey(key);
			const eventSpec = getEventSpec(key);
			if (eventSpec) {
				if (oldAttributes?.[key] !== value) {
					if (oldAttributes) container.removeEventListener(eventSpec[0], oldAttributes[key], eventSpec[1]);
					if (value != null) {
						if (typeof value !== "function") throw new Error(`Event handler for "${key}" is not a function`);
						container.addEventListener(eventSpec[0], value, eventSpec[1]);
					}
				}
			} else if (key === "dangerouslySetInnerHTML" && value) container.innerHTML = value.__html;
			else if (key === "ref") {
				refCleanupMap.get(container)?.();
				let cleanup;
				if (typeof value === "function") cleanup = value(container) || (() => value(null));
				else if (value && "current" in value) {
					value.current = container;
					cleanup = () => value.current = null;
				}
				refCleanupMap.set(container, cleanup);
			} else if (key === "style") {
				const style = container.style;
				if (typeof value === "string") style.cssText = value;
				else {
					style.cssText = "";
					if (value != null) styleObjectForEach(value, style.setProperty.bind(style));
				}
			} else {
				if (key === "value") {
					const nodeName = container.nodeName;
					if (nodeName === "SELECT") continue;
					else if (nodeName === "INPUT" || nodeName === "TEXTAREA") {
						container.value = normalizeFormValue(value);
						if (nodeName === "TEXTAREA") {
							container.textContent = value;
							continue;
						}
					}
				} else if (key === "checked" && container.nodeName === "INPUT" || key === "selected" && container.nodeName === "OPTION") container[key] = value;
				const k = toAttributeName(container, key);
				try {
					if (value === null || value === void 0 || value === false) container.removeAttribute(k);
					else if (value === true) container.setAttribute(k, "");
					else if (typeof value === "string" || typeof value === "number") container.setAttribute(k, value);
					else container.setAttribute(k, value.toString());
				} catch (e) {
					if (!isIgnorableAttributeError(e)) throw e;
				}
			}
		}
	}
	if (oldAttributes) for (let key in oldAttributes) {
		const value = oldAttributes[key];
		if (key !== "children" && !(key in attributes)) {
			key = normalizeIntrinsicElementKey(key);
			const eventSpec = getEventSpec(key);
			if (eventSpec) container.removeEventListener(eventSpec[0], value, eventSpec[1]);
			else if (key === "ref") {
				refCleanupMap.get(container)?.();
				refCleanupMap.delete(container);
			} else try {
				container.removeAttribute(toAttributeName(container, key));
			} catch (e) {
				if (!isIgnorableAttributeError(e)) throw e;
			}
		}
	}
};
const invokeTag = (context, node) => {
	node[DOM_STASH][0] = 0;
	buildDataStack.push([context, node]);
	const func = node.tag[DOM_RENDERER] || node.tag;
	const props = func.defaultProps ? {
		...func.defaultProps,
		...node.props
	} : node.props;
	try {
		return [func.call(null, props)];
	} finally {
		buildDataStack.pop();
	}
};
const getNextChildren = (node, container, nextChildren, childrenToRemove, callbacks) => {
	if (node.vR?.length) {
		childrenToRemove.push(...node.vR);
		delete node.vR;
	}
	if (typeof node.tag === "function") node[DOM_STASH][1][1]?.forEach((data) => callbacks.push(data));
	node.vC.forEach((child) => {
		if (isNodeString(child)) nextChildren.push(child);
		else if (typeof child.tag === "function" || child.tag === "") {
			child.c = container;
			const currentNextChildrenIndex = nextChildren.length;
			getNextChildren(child, container, nextChildren, childrenToRemove, callbacks);
			if (child.s) {
				for (let i = currentNextChildrenIndex; i < nextChildren.length; i++) nextChildren[i].s = true;
				child.s = false;
			}
		} else {
			nextChildren.push(child);
			if (child.vR?.length) {
				childrenToRemove.push(...child.vR);
				delete child.vR;
			}
		}
	});
};
const findInsertBefore = (node) => {
	while (node && (node.tag === HONO_PORTAL_ELEMENT || !node.e)) node = node.tag === HONO_PORTAL_ELEMENT || !node.vC?.[0] ? node.nN : node.vC[0];
	return node?.e;
};
const removeNode = (node) => {
	if (!isNodeString(node)) {
		node[DOM_STASH]?.[1][1]?.forEach((data) => data[2]?.());
		refCleanupMap.get(node.e)?.();
		if (node.p === 2) node.vC?.forEach((n) => n.p = 2);
		node.vC?.forEach(removeNode);
	}
	if (!node.p) {
		node.e?.remove();
		delete node.e;
	}
	if (typeof node.tag === "function") {
		updateMap.delete(node);
		fallbackUpdateFnArrayMap.delete(node);
		delete node[DOM_STASH][3];
		node.a = true;
	}
};
const apply = (node, container, isNew) => {
	node.c = container;
	applyNodeObject(node, container, isNew);
};
const findChildNodeIndex = (childNodes, child) => {
	if (!child) return;
	for (let i = 0, len = childNodes.length; i < len; i++) if (childNodes[i] === child) return i;
};
const cancelBuild = Symbol();
const applyNodeObject = (node, container, isNew) => {
	const next = [];
	const remove = [];
	const callbacks = [];
	getNextChildren(node, container, next, remove, callbacks);
	remove.forEach(removeNode);
	const childNodes = isNew ? void 0 : container.childNodes;
	let offset;
	let insertBeforeNode = null;
	if (isNew) offset = -1;
	else if (!childNodes.length) offset = 0;
	else {
		const offsetByNextNode = findChildNodeIndex(childNodes, findInsertBefore(node.nN));
		if (offsetByNextNode !== void 0) {
			insertBeforeNode = childNodes[offsetByNextNode];
			offset = offsetByNextNode;
		} else offset = findChildNodeIndex(childNodes, next.find((n) => n.tag !== HONO_PORTAL_ELEMENT && n.e)?.e) ?? -1;
		if (offset === -1) isNew = true;
	}
	for (let i = 0, len = next.length; i < len; i++, offset++) {
		const child = next[i];
		let el;
		if (child.s && child.e) {
			el = child.e;
			child.s = false;
		} else {
			const isNewLocal = isNew || !child.e;
			if (isNodeString(child)) {
				if (child.e && child.d) child.e.textContent = child.t;
				child.d = false;
				el = child.e ||= document.createTextNode(child.t);
			} else {
				el = child.e ||= child.n ? document.createElementNS(child.n, child.tag) : document.createElement(child.tag);
				applyProps(el, child.props, child.pP);
				applyNodeObject(child, el, isNewLocal);
				if (child.tag === "select") applySelectValue(el, child.props);
			}
		}
		if (child.tag === HONO_PORTAL_ELEMENT) offset--;
		else if (isNew) {
			if (!el.parentNode) container.appendChild(el);
		} else if (childNodes[offset] !== el && childNodes[offset - 1] !== el) {
			if (childNodes[offset + 1] === el) container.appendChild(childNodes[offset]);
			else container.insertBefore(el, insertBeforeNode || childNodes[offset] || null);
		}
	}
	if (node.pP) node.pP = void 0;
	if (callbacks.length) {
		const useLayoutEffectCbs = [];
		const useEffectCbs = [];
		callbacks.forEach(([, useLayoutEffectCb, , useEffectCb, useInsertionEffectCb]) => {
			if (useLayoutEffectCb) useLayoutEffectCbs.push(useLayoutEffectCb);
			if (useEffectCb) useEffectCbs.push(useEffectCb);
			useInsertionEffectCb?.();
		});
		useLayoutEffectCbs.forEach((cb) => cb());
		if (useEffectCbs.length) requestAnimationFrame(() => {
			useEffectCbs.forEach((cb) => cb());
		});
	}
};
const isSameContext = (oldContexts, newContexts) => !!(oldContexts && oldContexts.length === newContexts.length && oldContexts.every((ctx, i) => ctx[1] === newContexts[i][1]));
const indexChildrenByKey = (children) => {
	const index = /* @__PURE__ */ new Map();
	for (const child of children) {
		const key = child.key;
		if (index.has(key)) return;
		index.set(key, child);
	}
	return index;
};
const fallbackUpdateFnArrayMap = /* @__PURE__ */ new WeakMap();
const build = (context, node, children) => {
	const buildWithPreviousChildren = !children && node.pC;
	if (children) node.pC ||= node.vC;
	let foundErrorHandler;
	try {
		children ||= typeof node.tag == "function" ? invokeTag(context, node) : toArray(node.props.children);
		if (children[0]?.tag === "" && children[0][DOM_ERROR_HANDLER]) {
			foundErrorHandler = children[0][DOM_ERROR_HANDLER];
			context[5].push([
				context,
				foundErrorHandler,
				node
			]);
		}
		let oldVChildren = buildWithPreviousChildren ? [...node.pC] : node.vC ? [...node.vC] : void 0;
		const vChildren = [];
		let prevNode;
		let scanBudget = (oldVChildren?.length || 0) * 2;
		let oldChildrenByKey;
		for (let i = 0; i < children.length; i++) {
			if (Array.isArray(children[i])) {
				children.splice(i, 1, ...children[i].flat(Infinity));
				i--;
				continue;
			}
			let child = buildNode(children[i]);
			if (child) {
				if (typeof child.tag === "function" && !child.tag[DOM_INTERNAL_TAG]) {
					if (globalContexts.length > 0) child[DOM_STASH][2] = globalContexts.map((c) => [c, c.values.at(-1)]);
					if (context[5]?.length) child[DOM_STASH][3] = context[5].at(-1);
				}
				let oldChild;
				if (oldChildrenByKey && child.key === void 0 && !isNodeString(child)) {
					oldVChildren = [...oldChildrenByKey.values()];
					oldChildrenByKey = void 0;
				}
				if (oldChildrenByKey) {
					const key = child.key;
					const candidate = oldChildrenByKey.get(key);
					if (candidate && (isNodeString(child) ? isNodeString(candidate) : candidate.tag === child.tag && candidate.key === key)) {
						oldChild = candidate;
						oldChildrenByKey.delete(key);
					}
				} else if (oldVChildren && oldVChildren.length) {
					const first = oldVChildren[0];
					if (isNodeString(child) ? isNodeString(first) : !isNodeString(first) && (child.key !== void 0 ? first.key === child.key && first.tag === child.tag : first.tag === child.tag)) oldChild = oldVChildren.shift();
					else {
						const i = oldVChildren.findIndex(isNodeString(child) ? (c) => isNodeString(c) : child.key !== void 0 ? (c) => c.key === child.key && c.tag === child.tag : (c) => c.tag === child.tag);
						scanBudget -= i === -1 ? oldVChildren.length : i;
						if (i !== -1) {
							oldChild = oldVChildren[i];
							oldVChildren.splice(i, 1);
						}
						if (scanBudget < 0) {
							scanBudget = Infinity;
							oldChildrenByKey = indexChildrenByKey(oldVChildren);
						}
					}
				}
				if (oldChild) {
					if (isNodeString(child)) {
						if (oldChild.t !== child.t) {
							oldChild.t = child.t;
							oldChild.d = true;
						}
						child = oldChild;
					} else {
						const pP = oldChild.pP = oldChild.props;
						oldChild.props = child.props;
						oldChild.f ||= child.f || node.f;
						if (typeof child.tag === "function") {
							const oldContexts = oldChild[DOM_STASH][2];
							oldChild[DOM_STASH][2] = child[DOM_STASH][2] || [];
							oldChild[DOM_STASH][3] = child[DOM_STASH][3];
							if (!oldChild.f && ((oldChild.o || oldChild) === child.o || oldChild.tag[DOM_MEMO]?.(pP, oldChild.props)) && isSameContext(oldContexts, oldChild[DOM_STASH][2])) oldChild.s = true;
						}
						child = oldChild;
					}
				} else if (!isNodeString(child) && nameSpaceContext) {
					const ns = useContext(nameSpaceContext);
					if (ns) child.n = ns;
				}
				if (!isNodeString(child) && !child.s) {
					build(context, child);
					delete child.f;
				}
				vChildren.push(child);
				if (prevNode && !prevNode.s && !child.s) for (let p = prevNode; p && !isNodeString(p); p = p.vC?.at(-1)) p.nN = child;
				prevNode = child;
			}
		}
		oldVChildren = oldChildrenByKey ? [...oldChildrenByKey.values()] : oldVChildren;
		node.vR = buildWithPreviousChildren ? [...node.vC, ...oldVChildren || []] : oldVChildren || [];
		node.vC = vChildren;
		if (buildWithPreviousChildren) delete node.pC;
	} catch (e) {
		node.f = true;
		if (e === cancelBuild) {
			if (foundErrorHandler) return;
			else throw e;
		}
		const [errorHandlerContext, errorHandler, errorHandlerNode] = node[DOM_STASH]?.[3] || [];
		if (errorHandler) {
			const fallbackUpdateFn = () => update([
				0,
				false,
				context[2]
			], errorHandlerNode);
			const fallbackUpdateFnArray = fallbackUpdateFnArrayMap.get(errorHandlerNode) || [];
			fallbackUpdateFnArray.push(fallbackUpdateFn);
			fallbackUpdateFnArrayMap.set(errorHandlerNode, fallbackUpdateFnArray);
			const fallback = errorHandler(e, () => {
				const fnArray = fallbackUpdateFnArrayMap.get(errorHandlerNode);
				if (fnArray) {
					const i = fnArray.indexOf(fallbackUpdateFn);
					if (i !== -1) {
						fnArray.splice(i, 1);
						return fallbackUpdateFn();
					}
				}
			});
			if (fallback) {
				if (context[0] === 1) context[1] = true;
				else {
					build(context, errorHandlerNode, [fallback]);
					if ((errorHandler.length === 1 || context !== errorHandlerContext) && errorHandlerNode.c) {
						apply(errorHandlerNode, errorHandlerNode.c, false);
						return;
					}
				}
				throw cancelBuild;
			}
		}
		throw e;
	} finally {
		if (foundErrorHandler) context[5].pop();
	}
};
const buildNode = (node) => {
	if (node === void 0 || node === null || typeof node === "boolean") return;
	else if (typeof node === "string" || typeof node === "number") return {
		t: node.toString(),
		d: true
	};
	else {
		if ("vR" in node) node = {
			tag: node.tag,
			props: node.props,
			key: node.key,
			f: node.f,
			type: node.tag,
			ref: node.props.ref,
			o: node.o || node
		};
		if (typeof node.tag === "function") node[DOM_STASH] = [0, []];
		else {
			const ns = nameSpaceMap[node.tag];
			if (ns) {
				nameSpaceContext ||= createContext("");
				node.props.children = [{
					tag: nameSpaceContext,
					props: {
						value: node.n = `http://www.w3.org/${ns}`,
						children: node.props.children
					}
				}];
			}
		}
		return node;
	}
};
const replaceContainer = (node, from, to) => {
	if (node.c === from) {
		node.c = to;
		node.vC.forEach((child) => replaceContainer(child, from, to));
	}
};
const updateSync = (context, node) => {
	node[DOM_STASH][2]?.forEach(([c, v]) => {
		c.values.push(v);
	});
	try {
		build(context, node, void 0);
	} catch {
		return;
	}
	if (node.a) {
		delete node.a;
		return;
	}
	node[DOM_STASH][2]?.forEach(([c]) => {
		c.values.pop();
	});
	if (context[0] !== 1 || !context[1]) apply(node, node.c, false);
};
const updateMap = /* @__PURE__ */ new WeakMap();
const currentUpdateSets = [];
const update = async (context, node) => {
	context[5] ||= [];
	const existing = updateMap.get(node);
	if (existing) existing[0](void 0);
	let resolve;
	const promise = new Promise((r) => resolve = r);
	updateMap.set(node, [resolve, () => {
		if (context[2]) context[2](context, node, (context) => {
			updateSync(context, node);
		}).then(() => resolve(node));
		else {
			updateSync(context, node);
			resolve(node);
		}
	}]);
	if (currentUpdateSets.length) currentUpdateSets.at(-1).add(node);
	else {
		await Promise.resolve();
		const latest = updateMap.get(node);
		if (latest) {
			updateMap.delete(node);
			latest[1]();
		}
	}
	return promise;
};
const renderNode = (node, container) => {
	const context = [];
	context[5] = [];
	context[4] = true;
	build(context, node, void 0);
	context[4] = false;
	const fragment = document.createDocumentFragment();
	apply(node, fragment, true);
	replaceContainer(node, fragment, container);
	container.replaceChildren(fragment);
};
const render = (jsxNode, container) => {
	renderNode(buildNode({
		tag: "",
		props: { children: jsxNode }
	}), container);
};
const flushSync = (callback) => {
	const set = /* @__PURE__ */ new Set();
	currentUpdateSets.push(set);
	callback();
	set.forEach((node) => {
		const latest = updateMap.get(node);
		if (latest) {
			updateMap.delete(node);
			latest[1]();
		}
	});
	currentUpdateSets.pop();
};
const createPortal = (children, container, key) => ({
	tag: HONO_PORTAL_ELEMENT,
	props: { children },
	key,
	e: container,
	p: 1
});
//#endregion
export { build, buildDataStack, buildNode, createPortal, flushSync, getNameSpaceContext, render, renderNode, update };
