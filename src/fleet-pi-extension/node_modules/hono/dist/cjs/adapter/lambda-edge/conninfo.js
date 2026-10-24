Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/adapter/lambda-edge/conninfo.ts
/**
* @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
*/
const getConnInfo = (c) => ({ remote: { address: c.env.event.Records[0].cf.request.clientIp } });
//#endregion
exports.getConnInfo = getConnInfo;
