import { decode, sign, verify, verifyWithJwks } from "./jwt.js";
//#region src/utils/jwt/index.ts
/**
* @module
* JWT utility.
*/
const Jwt = {
	sign,
	verify,
	decode,
	verifyWithJwks
};
//#endregion
export { Jwt };
