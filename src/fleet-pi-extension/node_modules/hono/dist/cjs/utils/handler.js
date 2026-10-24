Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_constants = require("./constants.js");
//#region src/utils/handler.ts
/**
* @module
* Handler utility.
*/
const isMiddleware = (handler) => handler.length > 1;
const findTargetHandler = (handler) => {
	return handler["__COMPOSED_HANDLER"] ? findTargetHandler(handler[require_utils_constants.COMPOSED_HANDLER]) : handler;
};
//#endregion
exports.findTargetHandler = findTargetHandler;
exports.isMiddleware = isMiddleware;
