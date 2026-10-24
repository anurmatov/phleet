Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/utils/jwt/jwa.ts
/**
* @module
* JSON Web Algorithms (JWA)
* https://datatracker.ietf.org/doc/html/rfc7518
*/
let AlgorithmTypes = /* @__PURE__ */ function(AlgorithmTypes) {
	AlgorithmTypes["HS256"] = "HS256";
	AlgorithmTypes["HS384"] = "HS384";
	AlgorithmTypes["HS512"] = "HS512";
	AlgorithmTypes["RS256"] = "RS256";
	AlgorithmTypes["RS384"] = "RS384";
	AlgorithmTypes["RS512"] = "RS512";
	AlgorithmTypes["PS256"] = "PS256";
	AlgorithmTypes["PS384"] = "PS384";
	AlgorithmTypes["PS512"] = "PS512";
	AlgorithmTypes["ES256"] = "ES256";
	AlgorithmTypes["ES384"] = "ES384";
	AlgorithmTypes["ES512"] = "ES512";
	AlgorithmTypes["EdDSA"] = "EdDSA";
	return AlgorithmTypes;
}({});
//#endregion
exports.AlgorithmTypes = AlgorithmTypes;
