import { RequestIdVariables, requestId } from "./request-id.js";
//#region src/middleware/request-id/index.d.ts
declare module '../..' {
  interface ContextVariableMap extends RequestIdVariables {}
}
//#endregion
export { type RequestIdVariables, requestId };
export {};
