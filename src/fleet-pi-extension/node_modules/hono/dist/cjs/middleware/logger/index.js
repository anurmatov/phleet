Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_color = require("../../utils/color.js");
//#region src/middleware/logger/index.ts
const humanize = (times) => {
	const [delimiter, separator] = [",", "."];
	return times.map((v) => v.replace(/(\d)(?=(\d\d\d)+(?!\d))/g, "$1" + delimiter)).join(separator);
};
const time = (start) => {
	const delta = Date.now() - start;
	return humanize([delta < 1e3 ? delta + "ms" : Math.round(delta / 1e3) + "s"]);
};
const colorStatus = async (status) => {
	if (await require_utils_color.getColorEnabledAsync()) switch (status / 100 | 0) {
		case 5: return `\x1b[31m${status}\x1b[0m`;
		case 4: return `\x1b[33m${status}\x1b[0m`;
		case 3: return `\x1b[36m${status}\x1b[0m`;
		case 2: return `\x1b[32m${status}\x1b[0m`;
	}
	return `${status}`;
};
async function log(fn, prefix, method, path, status = 0, elapsed) {
	fn(prefix === "<--" ? `${prefix} ${method} ${path}` : `${prefix} ${method} ${path} ${await colorStatus(status)} ${elapsed}`);
}
/**
* Logger Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/logger}
*
* @param {PrintFunc} [fn=console.log] - Optional function for customized logging behavior.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* app.use(logger())
* app.get('/', (c) => c.text('Hello Hono!'))
* ```
*/
const logger = (fn = console.log) => {
	return async function logger(c, next) {
		const { method, url } = c.req;
		const path = url.slice(url.indexOf("/", 8));
		await log(fn, "<--", method, path);
		const start = Date.now();
		await next();
		await log(fn, "-->", method, path, c.res.status, time(start));
	};
};
//#endregion
exports.logger = logger;
