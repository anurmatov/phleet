Object.defineProperties(exports, {
	__esModule: { value: true },
	[Symbol.toStringTag]: { value: "Module" }
});
const require_jsx_constants = require("../constants.js");
const require_jsx_dom_context = require("./context.js");
const require_jsx_context = require("../context.js");
const require_jsx_children = require("../children.js");
const require_jsx_base = require("../base.js");
const require_jsx_dom_render = require("./render.js");
const require_jsx_hooks_index = require("../hooks/index.js");
const require_jsx_dom_hooks_index = require("./hooks/index.js");
const require_jsx_dom_jsx_dev_runtime = require("./jsx-dev-runtime.js");
require("./jsx-runtime.js");
const require_jsx_dom_components = require("./components.js");
//#region src/jsx/dom/index.ts
/**
* @module
* This module provides APIs for `hono/jsx/dom`.
*/
const createElement = (tag, props, ...children) => {
	const jsxProps = props ? { ...props } : {};
	if (children.length) jsxProps.children = children.length === 1 ? children[0] : children;
	let key = void 0;
	if ("key" in jsxProps) {
		key = jsxProps.key;
		delete jsxProps.key;
	}
	return require_jsx_dom_jsx_dev_runtime.jsxDEV(tag, jsxProps, key);
};
const cloneElement = (element, props, ...children) => {
	return require_jsx_dom_jsx_dev_runtime.jsxDEV(element.tag, {
		...element.props,
		...props,
		children: children.length ? children : element.props.children
	}, element.key);
};
const memo = (component, propsAreEqual = require_jsx_base.shallowEqual) => {
	const wrapper = ((props) => component(props));
	wrapper[require_jsx_constants.DOM_MEMO] = propsAreEqual;
	return wrapper;
};
var dom_default = {
	version: require_jsx_base.reactAPICompatVersion,
	useState: require_jsx_hooks_index.useState,
	useEffect: require_jsx_hooks_index.useEffect,
	useRef: require_jsx_hooks_index.useRef,
	useCallback: require_jsx_hooks_index.useCallback,
	use: require_jsx_hooks_index.use,
	startTransition: require_jsx_hooks_index.startTransition,
	useTransition: require_jsx_hooks_index.useTransition,
	useDeferredValue: require_jsx_hooks_index.useDeferredValue,
	startViewTransition: require_jsx_hooks_index.startViewTransition,
	useViewTransition: require_jsx_hooks_index.useViewTransition,
	useMemo: require_jsx_hooks_index.useMemo,
	useLayoutEffect: require_jsx_hooks_index.useLayoutEffect,
	useInsertionEffect: require_jsx_hooks_index.useInsertionEffect,
	useReducer: require_jsx_hooks_index.useReducer,
	useId: require_jsx_hooks_index.useId,
	useDebugValue: require_jsx_hooks_index.useDebugValue,
	createRef: require_jsx_hooks_index.createRef,
	forwardRef: require_jsx_hooks_index.forwardRef,
	useImperativeHandle: require_jsx_hooks_index.useImperativeHandle,
	useSyncExternalStore: require_jsx_hooks_index.useSyncExternalStore,
	useFormStatus: require_jsx_dom_hooks_index.useFormStatus,
	useActionState: require_jsx_dom_hooks_index.useActionState,
	useOptimistic: require_jsx_dom_hooks_index.useOptimistic,
	Suspense: require_jsx_dom_components.Suspense,
	ErrorBoundary: require_jsx_dom_components.ErrorBoundary,
	createContext: require_jsx_dom_context.createContext,
	useContext: require_jsx_context.useContext,
	memo,
	isValidElement: require_jsx_base.isValidElement,
	createElement,
	cloneElement,
	Children: require_jsx_children.Children,
	Fragment: require_jsx_dom_jsx_dev_runtime.Fragment,
	StrictMode: require_jsx_dom_jsx_dev_runtime.Fragment,
	flushSync: require_jsx_dom_render.flushSync,
	createPortal: require_jsx_dom_render.createPortal
};
//#endregion
exports.Children = require_jsx_children.Children;
exports.ErrorBoundary = require_jsx_dom_components.ErrorBoundary;
exports.Fragment = require_jsx_dom_jsx_dev_runtime.Fragment;
exports.StrictMode = require_jsx_dom_jsx_dev_runtime.Fragment;
exports.Suspense = require_jsx_dom_components.Suspense;
exports.cloneElement = cloneElement;
exports.createContext = require_jsx_dom_context.createContext;
exports.createElement = createElement;
exports.jsx = createElement;
exports.createPortal = require_jsx_dom_render.createPortal;
exports.createRef = require_jsx_hooks_index.createRef;
exports.default = dom_default;
exports.flushSync = require_jsx_dom_render.flushSync;
exports.forwardRef = require_jsx_hooks_index.forwardRef;
exports.isValidElement = require_jsx_base.isValidElement;
exports.memo = memo;
exports.render = require_jsx_dom_render.render;
exports.startTransition = require_jsx_hooks_index.startTransition;
exports.startViewTransition = require_jsx_hooks_index.startViewTransition;
exports.use = require_jsx_hooks_index.use;
exports.useActionState = require_jsx_dom_hooks_index.useActionState;
exports.useCallback = require_jsx_hooks_index.useCallback;
exports.useContext = require_jsx_context.useContext;
exports.useDebugValue = require_jsx_hooks_index.useDebugValue;
exports.useDeferredValue = require_jsx_hooks_index.useDeferredValue;
exports.useEffect = require_jsx_hooks_index.useEffect;
exports.useFormStatus = require_jsx_dom_hooks_index.useFormStatus;
exports.useId = require_jsx_hooks_index.useId;
exports.useImperativeHandle = require_jsx_hooks_index.useImperativeHandle;
exports.useInsertionEffect = require_jsx_hooks_index.useInsertionEffect;
exports.useLayoutEffect = require_jsx_hooks_index.useLayoutEffect;
exports.useMemo = require_jsx_hooks_index.useMemo;
exports.useOptimistic = require_jsx_dom_hooks_index.useOptimistic;
exports.useReducer = require_jsx_hooks_index.useReducer;
exports.useRef = require_jsx_hooks_index.useRef;
exports.useState = require_jsx_hooks_index.useState;
exports.useSyncExternalStore = require_jsx_hooks_index.useSyncExternalStore;
exports.useTransition = require_jsx_hooks_index.useTransition;
exports.useViewTransition = require_jsx_hooks_index.useViewTransition;
exports.version = require_jsx_base.reactAPICompatVersion;
