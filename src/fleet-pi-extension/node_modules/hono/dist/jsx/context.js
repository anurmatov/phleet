import { raw } from "../utils/html.js";
import { DOM_RENDERER } from "./constants.js";
import { createContextProviderFunction } from "./dom/context.js";
import { JSXFragmentNode, isUntrustedObject, renderChildren, renderUntrustedObject } from "./base.js";
//#region src/jsx/context.ts
const globalContexts = [];
let alsProbed = false;
let asyncLocalStorage;
let fallbackStore;
let fallbackRendersInFlight = 0;
let warnedFallbackDefault = false;
const loadAsyncLocalStorage = () => {
	if (alsProbed) return asyncLocalStorage;
	alsProbed = true;
	const global = globalThis;
	let AsyncLocalStorage;
	for (const probe of [() => global.process?.getBuiltinModule?.("node:async_hooks")?.AsyncLocalStorage, () => global.process?.mainModule?.require?.("node:async_hooks")?.AsyncLocalStorage]) {
		try {
			AsyncLocalStorage = probe();
		} catch {}
		if (AsyncLocalStorage) break;
	}
	if (AsyncLocalStorage) asyncLocalStorage = new AsyncLocalStorage();
	return asyncLocalStorage;
};
const getCurrentStore = () => {
	return loadAsyncLocalStorage()?.getStore() || fallbackStore;
};
const warnIfStorelessAccess = () => {
	if (fallbackRendersInFlight > 0 && !warnedFallbackDefault) {
		warnedFallbackDefault = true;
		console.warn("hono/jsx: AsyncLocalStorage is unavailable in this runtime, so useContext() after an await in an async component falls back to the context default value during server-side rendering. To get provided values across await boundaries, use a runtime with AsyncLocalStorage (Node.js >= 20.16, Deno, Bun, or Cloudflare Workers with the nodejs_compat flag).");
	}
};
const getContextValuesIn = (store, context) => {
	if (!store) {
		warnIfStorelessAccess();
		return context.values;
	}
	let values = store.get(context);
	if (!values) {
		values = [context.values[0]];
		store.set(context, values);
	}
	return values;
};
const readContextValueIn = (store, context) => {
	if (!store) {
		warnIfStorelessAccess();
		return context.values.at(-1);
	}
	const values = store.get(context);
	return values?.length ? values.at(-1) : context.values[0];
};
const captureContextValues = (store) => (store ? globalContexts.filter((c) => store.has(c)) : globalContexts).map((c) => [c, readContextValueIn(store, c)]);
const resumeWithContextValues = (callback, store, contexts) => runWithRenderContext(() => {
	const currentStore = getCurrentStore();
	const valuesPerContext = contexts.map(([context, value]) => {
		const values = getContextValuesIn(currentStore, context);
		values.push(value);
		return values;
	});
	const popContextValues = () => {
		valuesPerContext.forEach((values) => {
			values.pop();
		});
	};
	try {
		const result = callback();
		if (result instanceof Promise) return result.finally(popContextValues);
		popContextValues();
		return result;
	} catch (e) {
		popContextValues();
		throw e;
	}
}, store);
/**
* Establish the request-scoped context store for a render.
*
* `resumeStore` continues a suspended subtree in the same store on the fallback
* path (ignored when `AsyncLocalStorage` is available, where isolation is
* automatic).
*
* Without `AsyncLocalStorage` a render can't be followed across `await`, so the
* store lives in `fallbackStore` only during synchronous work (mirroring
* React's request storage). Reading context after `await` then finds no store
* and falls back to the default value — never another request's value.
*/
const runWithRenderContext = (callback, resumeStore) => {
	if (getCurrentStore()) return callback();
	const store = resumeStore ?? /* @__PURE__ */ new WeakMap();
	const storage = loadAsyncLocalStorage();
	if (storage) return storage.run(store, callback);
	fallbackStore = store;
	let result;
	try {
		result = callback();
	} finally {
		fallbackStore = void 0;
	}
	if (!warnedFallbackDefault && result instanceof Promise) {
		fallbackRendersInFlight++;
		result = result.finally(() => {
			fallbackRendersInFlight--;
		});
	}
	return result;
};
/**
* Capture the current render store and return a resumer that re-establishes it
* around a deferred continuation (e.g. a re-render after a suspended promise
* settles). Shared by every suspension point so none reimplements it.
*/
const captureRenderContext = () => {
	const store = getCurrentStore();
	const contexts = captureContextValues(store);
	return (callback) => resumeWithContextValues(callback, store, contexts);
};
/**
* Create a context whose value can be provided with `<Context.Provider>` and
* read with {@link useContext}.
*
* Server-side renders are isolated per request, so a provided value never leaks
* into a concurrent request — even across `await` in an async component, when
* `AsyncLocalStorage` is available (Node.js >= 20.16, Deno, Bun, Cloudflare
* Workers with `nodejs_compat`). Without it, reading context after `await`
* returns the default value; synchronous components and `use()`-based
* suspension are unaffected.
*/
const createContext = (defaultValue) => {
	const values = [defaultValue];
	const context = ((props) => {
		const contextValues = getContextValuesIn(getCurrentStore(), context);
		contextValues.push(props.value);
		let rendered;
		try {
			rendered = typeof props.children === "string" ? renderChildren([props.children]) : isUntrustedObject(props.children) ? renderUntrustedObject(props.children) : props.children ? (Array.isArray(props.children) ? new JSXFragmentNode("", {}, props.children) : props.children).toString() : raw("");
		} catch (e) {
			contextValues.pop();
			throw e;
		}
		if (rendered instanceof Promise) return rendered.finally(() => contextValues.pop()).then((resString) => raw(resString, resString.callbacks));
		else {
			contextValues.pop();
			return raw(rendered);
		}
	});
	context.values = values;
	context.Provider = context;
	context[DOM_RENDERER] = createContextProviderFunction(values);
	globalContexts.push(context);
	return context;
};
/**
* Read the current value of a context created with {@link createContext}.
*
* Safe to call from async components after `await`. See {@link createContext}
* for the per-runtime isolation guarantees.
*/
const useContext = (context) => {
	return readContextValueIn(getCurrentStore(), context);
};
//#endregion
export { captureRenderContext, createContext, globalContexts, runWithRenderContext, useContext };
