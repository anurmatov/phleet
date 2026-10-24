import { DOM_MEMO } from "../constants.js";
import { createContext } from "./context.js";
import { useContext } from "../context.js";
import { Children } from "../children.js";
import { isValidElement, reactAPICompatVersion, shallowEqual } from "../base.js";
import { createPortal, flushSync, render } from "./render.js";
import { createRef, forwardRef, startTransition, startViewTransition, use, useCallback, useDebugValue, useDeferredValue, useEffect, useId, useImperativeHandle, useInsertionEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, useSyncExternalStore, useTransition, useViewTransition } from "../hooks/index.js";
import { useActionState, useFormStatus, useOptimistic } from "./hooks/index.js";
import { Fragment, jsxDEV } from "./jsx-dev-runtime.js";
import "./jsx-runtime.js";
import { ErrorBoundary, Suspense } from "./components.js";
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
	return jsxDEV(tag, jsxProps, key);
};
const cloneElement = (element, props, ...children) => {
	return jsxDEV(element.tag, {
		...element.props,
		...props,
		children: children.length ? children : element.props.children
	}, element.key);
};
const memo = (component, propsAreEqual = shallowEqual) => {
	const wrapper = ((props) => component(props));
	wrapper[DOM_MEMO] = propsAreEqual;
	return wrapper;
};
var dom_default = {
	version: reactAPICompatVersion,
	useState,
	useEffect,
	useRef,
	useCallback,
	use,
	startTransition,
	useTransition,
	useDeferredValue,
	startViewTransition,
	useViewTransition,
	useMemo,
	useLayoutEffect,
	useInsertionEffect,
	useReducer,
	useId,
	useDebugValue,
	createRef,
	forwardRef,
	useImperativeHandle,
	useSyncExternalStore,
	useFormStatus,
	useActionState,
	useOptimistic,
	Suspense,
	ErrorBoundary,
	createContext,
	useContext,
	memo,
	isValidElement,
	createElement,
	cloneElement,
	Children,
	Fragment,
	StrictMode: Fragment,
	flushSync,
	createPortal
};
//#endregion
export { Children, ErrorBoundary, Fragment, Fragment as StrictMode, Suspense, cloneElement, createContext, createElement, createElement as jsx, createPortal, createRef, dom_default as default, flushSync, forwardRef, isValidElement, memo, render, startTransition, startViewTransition, use, useActionState, useCallback, useContext, useDebugValue, useDeferredValue, useEffect, useFormStatus, useId, useImperativeHandle, useInsertionEffect, useLayoutEffect, useMemo, useOptimistic, useReducer, useRef, useState, useSyncExternalStore, useTransition, useViewTransition, reactAPICompatVersion as version };
