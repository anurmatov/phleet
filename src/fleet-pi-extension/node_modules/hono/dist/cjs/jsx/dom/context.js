Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_jsx_constants = require("../constants.js");
const require_jsx_dom_utils = require("./utils.js");
const require_jsx_context = require("../context.js");
//#region src/jsx/dom/context.ts
const createContextProviderFunction = (values) => ({ value, children }) => {
	if (!children) return;
	const props = { children: [{
		tag: require_jsx_dom_utils.setInternalTagFlag(() => {
			values.push(value);
		}),
		props: {}
	}] };
	if (Array.isArray(children)) props.children.push(...children.flat());
	else props.children.push(children);
	props.children.push({
		tag: require_jsx_dom_utils.setInternalTagFlag(() => {
			values.pop();
		}),
		props: {}
	});
	const res = {
		tag: "",
		props,
		type: ""
	};
	res[require_jsx_constants.DOM_ERROR_HANDLER] = (err) => {
		values.pop();
		throw err;
	};
	return res;
};
const createContext = (defaultValue) => {
	const values = [defaultValue];
	const context = createContextProviderFunction(values);
	context.values = values;
	context.Provider = context;
	require_jsx_context.globalContexts.push(context);
	return context;
};
//#endregion
exports.createContext = createContext;
exports.createContextProviderFunction = createContextProviderFunction;
