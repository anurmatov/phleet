Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_middleware_serve_static_index = require("../../middleware/serve-static/index.js");
let node_fs_promises = require("node:fs/promises");
let node_path = require("node:path");
//#region src/adapter/bun/serve-static.ts
/**
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const serveStatic = (options = {}) => {
	return async function serveStatic(c, next) {
		const getContent = async (path) => {
			const file = Bun.file(path);
			return await file.exists() ? file : null;
		};
		const isDir = async (path) => {
			let isDir;
			try {
				isDir = (await (0, node_fs_promises.stat)(path)).isDirectory();
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
