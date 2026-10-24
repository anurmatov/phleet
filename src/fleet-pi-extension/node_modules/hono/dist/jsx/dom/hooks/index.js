import { PERMALINK } from "../../constants.js";
import { createContext } from "../context.js";
import { useContext } from "../../context.js";
import { useCallback, useState } from "../../hooks/index.js";
//#region src/jsx/dom/hooks/index.ts
/**
* Provide hooks used only in jsx/dom
*/
const FormContext = createContext({
	pending: false,
	data: null,
	method: null,
	action: null
});
const actions = /* @__PURE__ */ new Set();
const registerAction = (action) => {
	actions.add(action);
	action.finally(() => actions.delete(action));
};
/**
* This hook returns the current form status
* @returns FormStatus
*/
const useFormStatus = () => {
	return useContext(FormContext);
};
/**
* This hook returns the current state and a function to update the state optimistically
* The current state is updated optimistically and then reverted to the original state when all actions are resolved
* @param state
* @param updateState
* @returns [T, (action: N) => void]
*/
const useOptimistic = (state, updateState) => {
	const [optimisticState, setOptimisticState] = useState(state);
	if (actions.size > 0) Promise.all(actions).finally(() => {
		setOptimisticState(state);
	});
	else setOptimisticState(state);
	return [optimisticState, useCallback((newData) => {
		setOptimisticState((currentState) => updateState(currentState, newData));
	}, [])];
};
/**
* This hook returns the current state and a function to update the state by form action
* @param fn
* @param initialState
* @param permalink
* @returns [T, (data: FormData) => void]
*/
const useActionState = (fn, initialState, permalink) => {
	const [state, setState] = useState(initialState);
	const actionState = async (data) => {
		setState(await fn(state, data));
	};
	actionState[PERMALINK] = permalink;
	return [state, actionState];
};
//#endregion
export { FormContext, registerAction, useActionState, useFormStatus, useOptimistic };
