Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_helper_ssg_utils = require("./utils.js");
//#region src/helper/ssg/middleware.ts
const SSG_CONTEXT = "HONO_SSG_CONTEXT";
const X_HONO_DISABLE_SSG_HEADER_KEY = "x-hono-disable-ssg";
/**
* @deprecated
* Use `X_HONO_DISABLE_SSG_HEADER_KEY` instead.
* This constant will be removed in the next minor version.
*/
const SSG_DISABLED_RESPONSE = (() => {
	try {
		return new Response("SSG is disabled", {
			status: 404,
			headers: { [X_HONO_DISABLE_SSG_HEADER_KEY]: "true" }
		});
	} catch {
		return null;
	}
})();
/**
* Define SSG Route
*/
const ssgParams = (params) => async (c, next) => {
	if (require_helper_ssg_utils.isDynamicRoute(c.req.path)) {
		c.req.raw.ssgParams = Array.isArray(params) ? params : await params(c);
		return c.notFound();
	}
	await next();
};
/**
* @experimental
* `isSSGContext` is an experimental feature.
* The API might be changed.
*/
const isSSGContext = (c) => !!c.env?.[SSG_CONTEXT];
/**
* @experimental
* `disableSSG` is an experimental feature.
* The API might be changed.
*/
const disableSSG = () => async function disableSSG(c, next) {
	if (isSSGContext(c)) {
		c.header(X_HONO_DISABLE_SSG_HEADER_KEY, "true");
		return c.notFound();
	}
	await next();
};
/**
* @experimental
* `onlySSG` is an experimental feature.
* The API might be changed.
*/
const onlySSG = () => async function onlySSG(c, next) {
	if (!isSSGContext(c)) return c.notFound();
	await next();
};
//#endregion
exports.SSG_CONTEXT = SSG_CONTEXT;
exports.SSG_DISABLED_RESPONSE = SSG_DISABLED_RESPONSE;
exports.X_HONO_DISABLE_SSG_HEADER_KEY = X_HONO_DISABLE_SSG_HEADER_KEY;
exports.disableSSG = disableSSG;
exports.isSSGContext = isSSGContext;
exports.onlySSG = onlySSG;
exports.ssgParams = ssgParams;
