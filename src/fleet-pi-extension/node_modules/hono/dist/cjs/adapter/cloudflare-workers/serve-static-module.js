Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_cloudflare_workers_serve_static = require("./serve-static.js");
//#region src/adapter/cloudflare-workers/serve-static-module.ts
const module$1 = (options) => {
	return require_adapter_cloudflare_workers_serve_static.serveStatic(options);
};
//#endregion
exports.serveStatic = module$1;
