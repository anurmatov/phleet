Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_middleware_serve_static_index = require("../../middleware/serve-static/index.js");
const require_adapter_cloudflare_workers_utils = require("./utils.js");
//#region src/adapter/cloudflare-workers/serve-static.ts
/**
* @deprecated
* `serveStatic` in the Cloudflare Workers adapter is deprecated.
* You can serve static files directly using Cloudflare Static Assets.
* @see https://developers.cloudflare.com/workers/static-assets/
* Cloudflare Static Assets is currently in open beta. If this doesn't work for you,
* please consider using Cloudflare Pages. You can start to create the Cloudflare Pages
* application with the `npm create hono@latest` command.
*/
const serveStatic = (options = {}) => {
	return async function serveStatic(c, next) {
		const getContent = async (path) => {
			return require_adapter_cloudflare_workers_utils.getContentFromKVAsset(path, {
				manifest: options.manifest,
				namespace: options.namespace ? options.namespace : c.env ? c.env.__STATIC_CONTENT : void 0
			});
		};
		return require_middleware_serve_static_index.serveStatic({
			...options,
			getContent
		})(c, next);
	};
};
//#endregion
exports.serveStatic = serveStatic;
