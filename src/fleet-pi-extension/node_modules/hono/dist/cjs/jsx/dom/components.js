Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_jsx_constants = require("../constants.js");
const require_jsx_dom_jsx_dev_runtime = require("./jsx-dev-runtime.js");
require("./jsx-runtime.js");
//#region src/jsx/dom/components.ts
const ErrorBoundary = (({ children, fallback, fallbackRender, onError }) => {
	const res = require_jsx_dom_jsx_dev_runtime.Fragment({ children });
	res[require_jsx_constants.DOM_ERROR_HANDLER] = (err) => {
		if (err instanceof Promise) throw err;
		onError?.(err);
		return fallbackRender?.(err) || fallback;
	};
	return res;
});
const Suspense = (({ children, fallback }) => {
	const res = require_jsx_dom_jsx_dev_runtime.Fragment({ children });
	res[require_jsx_constants.DOM_ERROR_HANDLER] = (err, retry) => {
		if (!(err instanceof Promise)) throw err;
		err.finally(retry);
		return fallback;
	};
	return res;
});
//#endregion
exports.ErrorBoundary = ErrorBoundary;
exports.Suspense = Suspense;
