Object.defineProperties(exports, {
	__esModule: { value: true },
	[Symbol.toStringTag]: { value: "Module" }
});
const require_jsx_context = require("./context.js");
const require_jsx_children = require("./children.js");
const require_jsx_base = require("./base.js");
const require_jsx_hooks_index = require("./hooks/index.js");
const require_jsx_dom_hooks_index = require("./dom/hooks/index.js");
const require_jsx_streaming = require("./streaming.js");
const require_jsx_components = require("./components.js");
//#region src/jsx/index.ts
/**
* @module
* JSX for Hono.
*/
var jsx_default = {
	version: require_jsx_base.reactAPICompatVersion,
	memo: require_jsx_base.memo,
	Fragment: require_jsx_base.Fragment,
	StrictMode: require_jsx_base.Fragment,
	isValidElement: require_jsx_base.isValidElement,
	createElement: require_jsx_base.jsx,
	cloneElement: require_jsx_base.cloneElement,
	ErrorBoundary: require_jsx_components.ErrorBoundary,
	createContext: require_jsx_context.createContext,
	useContext: require_jsx_context.useContext,
	useState: require_jsx_hooks_index.useState,
	useEffect: require_jsx_hooks_index.useEffect,
	useRef: require_jsx_hooks_index.useRef,
	useCallback: require_jsx_hooks_index.useCallback,
	useReducer: require_jsx_hooks_index.useReducer,
	useId: require_jsx_hooks_index.useId,
	useDebugValue: require_jsx_hooks_index.useDebugValue,
	use: require_jsx_hooks_index.use,
	startTransition: require_jsx_hooks_index.startTransition,
	useTransition: require_jsx_hooks_index.useTransition,
	useDeferredValue: require_jsx_hooks_index.useDeferredValue,
	startViewTransition: require_jsx_hooks_index.startViewTransition,
	useViewTransition: require_jsx_hooks_index.useViewTransition,
	useMemo: require_jsx_hooks_index.useMemo,
	useLayoutEffect: require_jsx_hooks_index.useLayoutEffect,
	useInsertionEffect: require_jsx_hooks_index.useInsertionEffect,
	createRef: require_jsx_hooks_index.createRef,
	forwardRef: require_jsx_hooks_index.forwardRef,
	useImperativeHandle: require_jsx_hooks_index.useImperativeHandle,
	useSyncExternalStore: require_jsx_hooks_index.useSyncExternalStore,
	useActionState: require_jsx_dom_hooks_index.useActionState,
	useOptimistic: require_jsx_dom_hooks_index.useOptimistic,
	Suspense: require_jsx_streaming.Suspense,
	Children: require_jsx_children.Children
};
//#endregion
exports.Children = require_jsx_children.Children;
exports.ErrorBoundary = require_jsx_components.ErrorBoundary;
exports.Fragment = require_jsx_base.Fragment;
exports.StrictMode = require_jsx_base.Fragment;
exports.Suspense = require_jsx_streaming.Suspense;
exports.cloneElement = require_jsx_base.cloneElement;
exports.createContext = require_jsx_context.createContext;
exports.createElement = require_jsx_base.jsx;
exports.jsx = require_jsx_base.jsx;
exports.createRef = require_jsx_hooks_index.createRef;
exports.default = jsx_default;
exports.forwardRef = require_jsx_hooks_index.forwardRef;
exports.isValidElement = require_jsx_base.isValidElement;
exports.memo = require_jsx_base.memo;
exports.startTransition = require_jsx_hooks_index.startTransition;
exports.startViewTransition = require_jsx_hooks_index.startViewTransition;
exports.use = require_jsx_hooks_index.use;
exports.useActionState = require_jsx_dom_hooks_index.useActionState;
exports.useCallback = require_jsx_hooks_index.useCallback;
exports.useContext = require_jsx_context.useContext;
exports.useDebugValue = require_jsx_hooks_index.useDebugValue;
exports.useDeferredValue = require_jsx_hooks_index.useDeferredValue;
exports.useEffect = require_jsx_hooks_index.useEffect;
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
