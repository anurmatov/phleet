import { serveStatic as serveStatic$1 } from "../../middleware/serve-static/index.js";
import { join } from "node:path";
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
