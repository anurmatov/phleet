import { createContext, useContext } from "./context.js";
import { Children } from "./children.js";
import { Fragment, cloneElement, isValidElement, jsx, memo, reactAPICompatVersion } from "./base.js";
import { createRef, forwardRef, startTransition, startViewTransition, use, useCallback, useDebugValue, useDeferredValue, useEffect, useId, useImperativeHandle, useInsertionEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, useSyncExternalStore, useTransition, useViewTransition } from "./hooks/index.js";
import { useActionState, useOptimistic } from "./dom/hooks/index.js";
import { Suspense } from "./streaming.js";
import { ErrorBoundary } from "./components.js";
//#region src/jsx/index.ts
/**
* @module
* JSX for Hono.
*/
var jsx_default = {
	version: reactAPICompatVersion,
	memo,
	Fragment,
	StrictMode: Fragment,
	isValidElement,
	createElement: jsx,
	cloneElement,
	ErrorBoundary,
	createContext,
	useContext,
	useState,
	useEffect,
	useRef,
	useCallback,
	useReducer,
	useId,
	useDebugValue,
	use,
	startTransition,
	useTransition,
	useDeferredValue,
	startViewTransition,
	useViewTransition,
	useMemo,
	useLayoutEffect,
	useInsertionEffect,
	createRef,
	forwardRef,
	useImperativeHandle,
	useSyncExternalStore,
	useActionState,
	useOptimistic,
	Suspense,
	Children
};
//#endregion
export { Children, ErrorBoundary, Fragment, Fragment as StrictMode, Suspense, cloneElement, createContext, jsx as createElement, jsx, createRef, jsx_default as default, forwardRef, isValidElement, memo, startTransition, startViewTransition, use, useActionState, useCallback, useContext, useDebugValue, useDeferredValue, useEffect, useId, useImperativeHandle, useInsertionEffect, useLayoutEffect, useMemo, useOptimistic, useReducer, useRef, useState, useSyncExternalStore, useTransition, useViewTransition, reactAPICompatVersion as version };
