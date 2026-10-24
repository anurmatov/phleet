import { serveStatic as serveStatic$1 } from "../../middleware/serve-static/index.js";
import { stat } from "node:fs/promises";
import { join } from "node:path";
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
				isDir = (await stat(path)).isDirectory();
			} catch {}
			return isDir;
		};
		return serveStatic$1({
			...options,
			getContent,
			join,
			isDir
		})(c, next);
	};
};
//#endregion
export { serveStatic };
