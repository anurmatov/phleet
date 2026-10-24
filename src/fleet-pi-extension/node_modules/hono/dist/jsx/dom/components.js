import { DOM_ERROR_HANDLER } from "../constants.js";
import { Fragment } from "./jsx-dev-runtime.js";
import "./jsx-runtime.js";
//#region src/jsx/dom/components.ts
const ErrorBoundary = (({ children, fallback, fallbackRender, onError }) => {
	const res = Fragment({ children });
	res[DOM_ERROR_HANDLER] = (err) => {
		if (err instanceof Promise) throw err;
		onError?.(err);
		return fallbackRender?.(err) || fallback;
	};
	return res;
});
const Suspense = (({ children, fallback }) => {
	const res = Fragment({ children });
	res[DOM_ERROR_HANDLER] = (err, retry) => {
		if (!(err instanceof Promise)) throw err;
		err.finally(retry);
		return fallback;
	};
	return res;
});
//#endregion
export { ErrorBoundary, Suspense };
