Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_jwt_jwt = require("./jwt.js");
//#region src/utils/jwt/index.ts
/**
* @module
* JWT utility.
*/
const Jwt = {
	sign: require_utils_jwt_jwt.sign,
	verify: require_utils_jwt_jwt.verify,
	decode: require_utils_jwt_jwt.decode,
	verifyWithJwks: require_utils_jwt_jwt.verifyWithJwks
};
//#endregion
exports.Jwt = Jwt;
