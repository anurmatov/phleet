Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_cookie = require("../../utils/cookie.js");
//#region src/helper/cookie/index.ts
const getCookie = (c, key, prefix) => {
	const cookie = c.req.raw.headers.get("Cookie");
	if (typeof key === "string") {
		if (!cookie) return;
		let finalKey = key;
		if (prefix === "secure") finalKey = "__Secure-" + key;
		else if (prefix === "host") finalKey = "__Host-" + key;
		return require_utils_cookie.parse(cookie, finalKey)[finalKey];
	}
	if (!cookie) return {};
	return require_utils_cookie.parse(cookie);
};
const getSignedCookie = async (c, secret, key, prefix) => {
	const cookie = c.req.raw.headers.get("Cookie");
	if (typeof key === "string") {
		if (!cookie) return;
		let finalKey = key;
		if (prefix === "secure") finalKey = "__Secure-" + key;
		else if (prefix === "host") finalKey = "__Host-" + key;
		return (await require_utils_cookie.parseSigned(cookie, secret, finalKey))[finalKey];
	}
	if (!cookie) return {};
	return await require_utils_cookie.parseSigned(cookie, secret);
};
const generateCookie = (name, value, opt) => {
	let cookie;
	if (opt?.prefix === "secure") cookie = require_utils_cookie.serialize("__Secure-" + name, value, {
		path: "/",
		...opt,
		secure: true
	});
	else if (opt?.prefix === "host") cookie = require_utils_cookie.serialize("__Host-" + name, value, {
		...opt,
		path: "/",
		secure: true,
		domain: void 0
	});
	else cookie = require_utils_cookie.serialize(name, value, {
		path: "/",
		...opt
	});
	return cookie;
};
const setCookie = (c, name, value, opt) => {
	const cookie = generateCookie(name, value, opt);
	c.header("Set-Cookie", cookie, { append: true });
};
const generateSignedCookie = async (name, value, secret, opt) => {
	let cookie;
	if (opt?.prefix === "secure") cookie = await require_utils_cookie.serializeSigned("__Secure-" + name, value, secret, {
		path: "/",
		...opt,
		secure: true
	});
	else if (opt?.prefix === "host") cookie = await require_utils_cookie.serializeSigned("__Host-" + name, value, secret, {
		...opt,
		path: "/",
		secure: true,
		domain: void 0
	});
	else cookie = await require_utils_cookie.serializeSigned(name, value, secret, {
		path: "/",
		...opt
	});
	return cookie;
};
const setSignedCookie = async (c, name, value, secret, opt) => {
	const cookie = await generateSignedCookie(name, value, secret, opt);
	c.header("set-cookie", cookie, { append: true });
};
const deleteCookie = (c, name, opt) => {
	const deletedCookie = getCookie(c, name, opt?.prefix);
	setCookie(c, name, "", {
		...opt,
		maxAge: 0
	});
	return deletedCookie;
};
//#endregion
exports.deleteCookie = deleteCookie;
exports.generateCookie = generateCookie;
exports.generateSignedCookie = generateSignedCookie;
exports.getCookie = getCookie;
exports.getSignedCookie = getSignedCookie;
exports.setCookie = setCookie;
exports.setSignedCookie = setSignedCookie;
