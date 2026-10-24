Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_client_fetch_result_please = require("./fetch-result-please.js");
//#region src/client/utils.ts
const mergePath = (base, path) => {
	base = base.replace(/\/+$/, "");
	base = base + "/";
	path = path.replace(/^\/+/, "");
	return base + path;
};
const replaceUrlParam = (urlString, params) => {
	for (const [k, v] of Object.entries(params)) {
		const reg = new RegExp("/:" + k + "(?:{[^/]+})?\\??(?=/|$)");
		urlString = urlString.replace(reg, () => v ? `/${v}` : "");
	}
	return urlString;
};
const buildSearchParams = (query) => {
	const searchParams = new URLSearchParams();
	for (const [k, v] of Object.entries(query)) {
		if (v === void 0) continue;
		if (Array.isArray(v)) for (const v2 of v) {
			if (v2 === void 0) continue;
			searchParams.append(k, v2);
		}
		else searchParams.set(k, v);
	}
	return searchParams;
};
const replaceUrlProtocol = (urlString, protocol) => {
	switch (protocol) {
		case "ws": return urlString.replace(/^http/, "ws");
		case "http": return urlString.replace(/^ws/, "http");
	}
};
const removeIndexString = (urlString) => {
	if (/^https?:\/\/[^\/]+?\/index(?=\?|$)/.test(urlString)) return urlString.replace(/\/index(?=\?|$)/, "/");
	return urlString.replace(/\/index(?=\?|$)/, "");
};
function isObject(item) {
	return typeof item === "object" && item !== null && !Array.isArray(item);
}
function deepMerge(target, source) {
	if (!isObject(target) && !isObject(source)) return source;
	const merged = { ...target };
	for (const key in source) {
		const value = source[key];
		if (isObject(merged[key]) && isObject(value)) merged[key] = deepMerge(merged[key], value);
		else merged[key] = value;
	}
	return merged;
}
/**
* Shortcut to get a consumable response from `hc`'s fetch calls (Response), with types inference.
*
* Smartly parse the response data, throwing a structured error if the response is not `ok`. ({@link DetailedError})
*
* @example const result = await parseResponse(client.posts.$get())
*/
async function parseResponse(fetchRes) {
	return require_client_fetch_result_please.fetchRP(fetchRes);
}
//#endregion
exports.DetailedError = require_client_fetch_result_please.DetailedError;
exports.buildSearchParams = buildSearchParams;
exports.deepMerge = deepMerge;
exports.mergePath = mergePath;
exports.parseResponse = parseResponse;
exports.removeIndexString = removeIndexString;
exports.replaceUrlParam = replaceUrlParam;
exports.replaceUrlProtocol = replaceUrlProtocol;
