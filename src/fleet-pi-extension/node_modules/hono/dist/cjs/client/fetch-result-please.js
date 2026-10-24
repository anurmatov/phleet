Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/client/fetch-result-please.ts
/**
* @description This file is a modified version of `fetch-result-please` (`ofetch`), minimalized and adapted to Hono's custom needs.
*
* @link https://www.npmjs.com/package/fetch-result-please
*/
const nullBodyResponses = /* @__PURE__ */ new Set([
	101,
	204,
	205,
	304
]);
/**
* Smartly parses and return the consumable result from a fetch `Response`.
*
* Throwing a structured error if the response is not `ok`. ({@link DetailedError})
*/
async function fetchRP(fetchRes) {
	const _fetchRes = await fetchRes;
	if ((_fetchRes.body || _fetchRes._bodyInit) && !nullBodyResponses.has(_fetchRes.status)) _fetchRes._data = await _fetchRes[detectResponseType(_fetchRes)]();
	if (!_fetchRes.ok) throw new DetailedError(`${_fetchRes.status} ${_fetchRes.statusText}`, {
		statusCode: _fetchRes?.status,
		detail: {
			data: _fetchRes?._data,
			statusText: _fetchRes?.statusText
		}
	});
	return _fetchRes._data;
}
var DetailedError = class extends Error {
	/**
	* Additional `message` that will be logged AND returned to client
	*/
	detail;
	/**
	* Additional `code` that will be logged AND returned to client
	*/
	code;
	/**
	* Additional value that will be logged AND NOT returned to client
	*/
	log;
	/**
	* Optionally set the status code to return, in a web server context
	*/
	statusCode;
	constructor(message, options = {}) {
		super(message);
		this.name = "DetailedError";
		this.log = options.log;
		this.detail = options.detail;
		this.code = options.code;
		this.statusCode = options.statusCode;
	}
};
const jsonRegex = /^application\/(?:[\w!#$%&*.^`~-]*\+)?json(?:;.+)?$/i;
function detectResponseType(response) {
	const _contentType = response.headers.get("content-type");
	if (!_contentType) return "text";
	const contentType = _contentType.split(";").shift();
	if (jsonRegex.test(contentType)) return "json";
	return "text";
}
//#endregion
exports.DetailedError = DetailedError;
exports.fetchRP = fetchRP;
