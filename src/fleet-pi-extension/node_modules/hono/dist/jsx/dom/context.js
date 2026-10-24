import { DOM_ERROR_HANDLER } from "../constants.js";
import { setInternalTagFlag } from "./utils.js";
import { globalContexts } from "../context.js";
//#region src/jsx/dom/context.ts
const createContextProviderFunction = (values) => ({ value, children }) => {
	if (!children) return;
	const props = { children: [{
		tag: setInternalTagFlag(() => {
			values.push(value);
		}),
		props: {}
	}] };
	if (Array.isArray(children)) props.children.push(...children.flat());
	else props.children.push(children);
	props.children.push({
		tag: setInternalTagFlag(() => {
			values.pop();
		}),
		props: {}
	});
	const res = {
		tag: "",
		props,
		type: ""
	};
	res[DOM_ERROR_HANDLER] = (err) => {
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
	globalContexts.push(context);
	return context;
};
//#endregion
export { createContext, createContextProviderFunction };
