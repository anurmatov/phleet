import { AlgorithmTypes } from "../../utils/jwt/jwa.js";
import { JwtVariables, decode, jwt, sign, verify, verifyWithJwks } from "./jwt.js";
//#region src/middleware/jwt/index.d.ts
declare module '../..' {
  interface ContextVariableMap extends JwtVariables<unknown> {}
}
//#endregion
export { AlgorithmTypes, type JwtVariables, decode, jwt, sign, verify, verifyWithJwks };
export {};
