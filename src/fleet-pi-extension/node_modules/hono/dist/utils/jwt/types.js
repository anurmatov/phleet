//#region src/utils/jwt/types.ts
/**
* @module
* Type definitions for JWT utilities.
*/
var JwtAlgorithmNotImplemented = class extends Error {
	constructor(alg) {
		super(`${alg} is not an implemented algorithm`);
		this.name = "JwtAlgorithmNotImplemented";
	}
};
var JwtAlgorithmRequired = class extends Error {
	constructor() {
		super("JWT verification requires \"alg\" option to be specified");
		this.name = "JwtAlgorithmRequired";
	}
};
var JwtAlgorithmMismatch = class extends Error {
	constructor(expected, actual) {
		super(`JWT algorithm mismatch: expected "${expected}", got "${actual}"`);
		this.name = "JwtAlgorithmMismatch";
	}
};
var JwtTokenInvalid = class extends Error {
	constructor(token) {
		super(`invalid JWT token: ${token}`);
		this.name = "JwtTokenInvalid";
	}
};
var JwtTokenNotBefore = class extends Error {
	constructor(token) {
		super(`token (${token}) is being used before it's valid`);
		this.name = "JwtTokenNotBefore";
	}
};
var JwtTokenExpired = class extends Error {
	constructor(token) {
		super(`token (${token}) expired`);
		this.name = "JwtTokenExpired";
	}
};
var JwtTokenIssuedAt = class extends Error {
	constructor(currentTimestamp, iat) {
		super(`Invalid "iat" claim, must be a valid number lower than "${currentTimestamp}" (iat: "${iat}")`);
		this.name = "JwtTokenIssuedAt";
	}
};
var JwtTokenIssuer = class extends Error {
	constructor(expected, iss) {
		super(`expected issuer "${expected}", got ${iss ? `"${iss}"` : "none"} `);
		this.name = "JwtTokenIssuer";
	}
};
var JwtHeaderInvalid = class extends Error {
	constructor(header) {
		super(`jwt header is invalid: ${JSON.stringify(header)}`);
		this.name = "JwtHeaderInvalid";
	}
};
var JwtHeaderRequiresKid = class extends Error {
	constructor(header) {
		super(`required "kid" in jwt header: ${JSON.stringify(header)}`);
		this.name = "JwtHeaderRequiresKid";
	}
};
var JwtSymmetricAlgorithmNotAllowed = class extends Error {
	constructor(alg) {
		super(`symmetric algorithm "${alg}" is not allowed for JWK verification`);
		this.name = "JwtSymmetricAlgorithmNotAllowed";
	}
};
var JwtAlgorithmNotAllowed = class extends Error {
	constructor(alg, allowedAlgorithms) {
		super(`algorithm "${alg}" is not in the allowed list: [${allowedAlgorithms.join(", ")}]`);
		this.name = "JwtAlgorithmNotAllowed";
	}
};
var JwtTokenSignatureMismatched = class extends Error {
	constructor(token) {
		super(`token(${token}) signature mismatched`);
		this.name = "JwtTokenSignatureMismatched";
	}
};
var JwtPayloadRequiresAud = class extends Error {
	constructor(payload) {
		super(`required "aud" in jwt payload: ${JSON.stringify(payload)}`);
		this.name = "JwtPayloadRequiresAud";
	}
};
var JwtTokenAudience = class extends Error {
	constructor(expected, aud) {
		super(`expected audience "${Array.isArray(expected) ? expected.join(", ") : expected}", got "${aud}"`);
		this.name = "JwtTokenAudience";
	}
};
let CryptoKeyUsage = /* @__PURE__ */ function(CryptoKeyUsage) {
	CryptoKeyUsage["Encrypt"] = "encrypt";
	CryptoKeyUsage["Decrypt"] = "decrypt";
	CryptoKeyUsage["Sign"] = "sign";
	CryptoKeyUsage["Verify"] = "verify";
	CryptoKeyUsage["DeriveKey"] = "deriveKey";
	CryptoKeyUsage["DeriveBits"] = "deriveBits";
	CryptoKeyUsage["WrapKey"] = "wrapKey";
	CryptoKeyUsage["UnwrapKey"] = "unwrapKey";
	return CryptoKeyUsage;
}({});
//#endregion
export { CryptoKeyUsage, JwtAlgorithmMismatch, JwtAlgorithmNotAllowed, JwtAlgorithmNotImplemented, JwtAlgorithmRequired, JwtHeaderInvalid, JwtHeaderRequiresKid, JwtPayloadRequiresAud, JwtSymmetricAlgorithmNotAllowed, JwtTokenAudience, JwtTokenExpired, JwtTokenInvalid, JwtTokenIssuedAt, JwtTokenIssuer, JwtTokenNotBefore, JwtTokenSignatureMismatched };
