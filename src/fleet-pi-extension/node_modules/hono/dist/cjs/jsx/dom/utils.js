Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_jsx_constants = require("../constants.js");
//#region src/jsx/dom/utils.ts
const setInternalTagFlag = (fn) => {
	fn[require_jsx_constants.DOM_INTERNAL_TAG] = true;
	return fn;
};
//#endregion
exports.setInternalTagFlag = setInternalTagFlag;
