import { DOM_INTERNAL_TAG } from "../constants.js";
//#region src/jsx/dom/utils.ts
const setInternalTagFlag = (fn) => {
	fn[DOM_INTERNAL_TAG] = true;
	return fn;
};
//#endregion
export { setInternalTagFlag };
