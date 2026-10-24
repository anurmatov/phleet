Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_accept = require("../../utils/accept.js");
//#region src/helper/accepts/accepts.ts
const matchType = (acceptType, supportedType) => {
	const accept = acceptType.toLowerCase();
	const supported = supportedType.toLowerCase();
	if (accept === supported) return true;
	if (accept === "*/*" || accept === "*") return false;
	if (accept.endsWith("/*")) {
		const [acceptMain] = accept.split("/");
		const [supportedMain] = supported.split("/");
		return acceptMain === supportedMain;
	}
	return false;
};
const getSpecificity = (type) => {
	if (type === "*/*" || type === "*") return 1;
	if (type.endsWith("/*")) return 2;
	return 3;
};
const defaultMatch = (accepts, config) => {
	const { supports, default: defaultSupport } = config;
	const sortedAccepts = accepts.filter((accept) => accept.q > 0).sort((a, b) => {
		if (b.q !== a.q) return b.q - a.q;
		return getSpecificity(b.type) - getSpecificity(a.type);
	});
	for (const accept of sortedAccepts) {
		const matched = supports.find((supported) => matchType(accept.type, supported));
		if (matched) return matched;
	}
	return defaultSupport;
};
/**
* Match the accept header with the given options.
* @example
* ```ts
* app.get('/users', (c) => {
*   const lang = accepts(c, {
*     header: 'Accept-Language',
*     supports: ['en', 'zh'],
*     default: 'en',
*   })
* })
* ```
*/
const accepts = (c, options) => {
	const acceptHeader = c.req.header(options.header);
	if (!acceptHeader) return options.default;
	const accepts = require_utils_accept.parseAccept(acceptHeader);
	return (options.match || defaultMatch)(accepts, options);
};
//#endregion
exports.accepts = accepts;
exports.defaultMatch = defaultMatch;
