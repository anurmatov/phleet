Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_encode = require("./encode.js");
//#region src/utils/basic-auth.ts
const CREDENTIALS_REGEXP = /^ *(?:[Bb][Aa][Ss][Ii][Cc]) +([A-Za-z0-9._~+/-]+=*) *$/;
const USER_PASS_REGEXP = /^([^:]*):(.*)$/;
const utf8Decoder = new TextDecoder();
const auth = (req) => {
	const match = CREDENTIALS_REGEXP.exec(req.headers.get("Authorization") || "");
	if (!match) return;
	let userPass = void 0;
	try {
		userPass = USER_PASS_REGEXP.exec(utf8Decoder.decode(require_utils_encode.decodeBase64(match[1])));
	} catch {}
	if (!userPass) return;
	return {
		username: userPass[1],
		password: userPass[2]
	};
};
//#endregion
exports.auth = auth;
