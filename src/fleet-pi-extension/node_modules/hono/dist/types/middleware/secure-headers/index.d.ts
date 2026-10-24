import { ContentSecurityPolicyOptionHandler, NONCE, SecureHeadersVariables, secureHeaders } from "./secure-headers.js";
//#region src/middleware/secure-headers/index.d.ts
declare module '../..' {
  interface ContextVariableMap extends SecureHeadersVariables {}
}
//#endregion
export { type ContentSecurityPolicyOptionHandler, NONCE, type SecureHeadersVariables, secureHeaders };
export {};
