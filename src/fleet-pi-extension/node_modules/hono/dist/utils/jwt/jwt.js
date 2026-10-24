import { decodeBase64Url, encodeBase64Url } from "../encode.js";
import { AlgorithmTypes } from "./jwa.js";
import { JwtAlgorithmMismatch, JwtAlgorithmNotAllowed, JwtAlgorithmRequired, JwtHeaderInvalid, JwtHeaderRequiresKid, JwtPayloadRequiresAud, JwtSymmetricAlgorithmNotAllowed, JwtTokenAudience, JwtTokenExpired, JwtTokenInvalid, JwtTokenIssuedAt, JwtTokenIssuer, JwtTokenNotBefore, JwtTokenSignatureMismatched } from "./types.js";
import { utf8Decoder, utf8Encoder } from "./utf8.js";
import { signing, verifying } from "./jws.js";
//#region src/utils/jwt/jwt.ts
/**
* @module
* JSON Web Token (JWT)
* https://datatracker.ietf.org/doc/html/rfc7519
*/
const encodeJwtPart = (part) => encodeBase64Url(utf8Encoder.encode(JSON.stringify(part)).buffer).replace(/=/g, "");
const encodeSignaturePart = (buf) => encodeBase64Url(buf).replace(/=/g, "");
const decodeJwtPart = (part) => JSON.parse(utf8Decoder.decode(decodeBase64Url(part)));
function isTokenHeader(obj) {
	if (typeof obj === "object" && obj !== null) {
		const objWithAlg = obj;
		return "alg" in objWithAlg && Object.values(AlgorithmTypes).includes(objWithAlg.alg) && (!("typ" in objWithAlg) || objWithAlg.typ === "JWT");
	}
	return false;
}
const sign = async (payload, privateKey, alg = "HS256") => {
	const encodedPayload = encodeJwtPart(payload);
	let encodedHeader;
	if (typeof privateKey === "object" && "alg" in privateKey) {
		alg = privateKey.alg;
		encodedHeader = encodeJwtPart({
			alg,
			typ: "JWT",
			kid: privateKey.kid
		});
	} else encodedHeader = encodeJwtPart({
		alg,
		typ: "JWT"
	});
	const partialToken = `${encodedHeader}.${encodedPayload}`;
	const signaturePart = await signing(privateKey, alg, utf8Encoder.encode(partialToken));
	return `${partialToken}.${encodeSignaturePart(signaturePart)}`;
};
const verify = async (token, publicKey, algOrOptions) => {
	if (!algOrOptions) throw new JwtAlgorithmRequired();
	const { alg, iss, nbf = true, exp = true, iat = true, aud } = typeof algOrOptions === "string" ? { alg: algOrOptions } : algOrOptions;
	if (!alg) throw new JwtAlgorithmRequired();
	const tokenParts = token.split(".");
	if (tokenParts.length !== 3) throw new JwtTokenInvalid(token);
	const { header, payload } = decode(token);
	if (!isTokenHeader(header)) throw new JwtHeaderInvalid(header);
	if (header.alg !== alg) throw new JwtAlgorithmMismatch(alg, header.alg);
	const now = Math.floor(Date.now() / 1e3);
	if (nbf && payload.nbf !== void 0) {
		if (typeof payload.nbf !== "number" || !Number.isFinite(payload.nbf) || payload.nbf > now) throw new JwtTokenNotBefore(token);
	}
	if (exp && payload.exp !== void 0) {
		if (typeof payload.exp !== "number" || !Number.isFinite(payload.exp) || payload.exp <= now) throw new JwtTokenExpired(token);
	}
	if (iat && payload.iat !== void 0) {
		if (typeof payload.iat !== "number" || !Number.isFinite(payload.iat) || now < payload.iat) throw new JwtTokenIssuedAt(now, payload.iat);
	}
	if (iss) {
		if (!payload.iss) throw new JwtTokenIssuer(iss, null);
		if (typeof iss === "string" && payload.iss !== iss) throw new JwtTokenIssuer(iss, payload.iss);
		if (iss instanceof RegExp && !iss.test(payload.iss)) throw new JwtTokenIssuer(iss, payload.iss);
	}
	if (aud) {
		if (!payload.aud) throw new JwtPayloadRequiresAud(payload);
		if (!(Array.isArray(payload.aud) ? payload.aud : [payload.aud]).some((payloadAud) => aud instanceof RegExp ? aud.test(payloadAud) : typeof aud === "string" ? payloadAud === aud : Array.isArray(aud) && aud.includes(payloadAud))) throw new JwtTokenAudience(aud, payload.aud);
	}
	const headerPayload = token.substring(0, token.lastIndexOf("."));
	let signature;
	try {
		signature = decodeBase64Url(tokenParts[2]);
	} catch {
		throw new JwtTokenInvalid(token);
	}
	if (!await verifying(publicKey, alg, signature, utf8Encoder.encode(headerPayload))) throw new JwtTokenSignatureMismatched(token);
	return payload;
};
const symmetricAlgorithms = [
	"HS256",
	"HS384",
	"HS512"
];
const verifyWithJwks = async (token, options, init) => {
	const verifyOpts = options.verification || {};
	const header = decodeHeader(token);
	if (!isTokenHeader(header)) throw new JwtHeaderInvalid(header);
	if (!header.kid) throw new JwtHeaderRequiresKid(header);
	if (symmetricAlgorithms.includes(header.alg)) throw new JwtSymmetricAlgorithmNotAllowed(header.alg);
	if (!options.allowedAlgorithms.includes(header.alg)) throw new JwtAlgorithmNotAllowed(header.alg, options.allowedAlgorithms);
	let verifyKeys = options.keys ? [...options.keys] : void 0;
	if (options.jwks_uri) {
		const response = await fetch(options.jwks_uri, init);
		if (!response.ok) throw new Error(`failed to fetch JWKS from ${options.jwks_uri}`);
		const data = await response.json();
		if (!data.keys) throw new Error("invalid JWKS response. \"keys\" field is missing");
		if (!Array.isArray(data.keys)) throw new Error("invalid JWKS response. \"keys\" field is not an array");
		verifyKeys ??= [];
		verifyKeys.push(...data.keys);
	} else if (!verifyKeys) throw new Error("verifyWithJwks requires options for either \"keys\" or \"jwks_uri\" or both");
	const matchingKey = verifyKeys.find((key) => key.kid === header.kid);
	if (!matchingKey) throw new JwtTokenInvalid(token);
	if (matchingKey.alg && matchingKey.alg !== header.alg) throw new JwtAlgorithmMismatch(matchingKey.alg, header.alg);
	return await verify(token, matchingKey, {
		alg: header.alg,
		...verifyOpts
	});
};
const decode = (token) => {
	const parts = token.split(".");
	if (parts.length !== 3) throw new JwtTokenInvalid(token);
	try {
		return {
			header: decodeJwtPart(parts[0]),
			payload: decodeJwtPart(parts[1])
		};
	} catch {
		throw new JwtTokenInvalid(token);
	}
};
const decodeHeader = (token) => {
	const parts = token.split(".");
	if (parts.length !== 3) throw new JwtTokenInvalid(token);
	try {
		return decodeJwtPart(parts[0]);
	} catch {
		throw new JwtTokenInvalid(token);
	}
};
//#endregion
export { decode, decodeHeader, isTokenHeader, sign, verify, verifyWithJwks };
