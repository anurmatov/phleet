Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_middleware_serve_static_index = require("../../middleware/serve-static/index.js");
let node_path = require("node:path");
//#region src/adapter/deno/serve-static.ts
const { open, lstatSync, errors } = Deno;
/**
* @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
*/
const serveStatic = (options = {}) => {
	return async function serveStatic(c, next) {
		const getContent = async (path) => {
			try {
				if (isDir(path)) return null;
				return (await open(path)).readable;
			} catch (e) {
				if (!(e instanceof errors.NotFound)) console.warn(`${e}`);
				return null;
			}
		};
		const isDir = (path) => {
			let isDir;
			try {
				isDir = lstatSync(path).isDirectory;
			} catch {}
			return isDir;
		};
		return require_middleware_serve_static_index.serveStatic({
			...options,
			getContent,
			join: node_path.join,
			isDir
		})(c, next);
	};
};
//#endregion
exports.serveStatic = serveStatic;
