Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_helper_ssg_ssg = require("../../helper/ssg/ssg.js");
require("../../helper/ssg/index.js");
//#region src/adapter/deno/ssg.ts
/**
* @experimental
* `denoFileSystemModule` is an experimental feature.
* The API might be changed.
* @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
*/
const denoFileSystemModule = {
	writeFile: async (path, data) => {
		const uint8Data = typeof data === "string" ? new TextEncoder().encode(data) : new Uint8Array(data);
		await Deno.writeFile(path, uint8Data);
	},
	mkdir: async (path, options) => {
		return Deno.mkdir(path, { recursive: options?.recursive ?? false });
	}
};
/**
* @experimental
* `toSSG` is an experimental feature.
* The API might be changed.
* @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
*/
const toSSG = async (app, options) => {
	return require_helper_ssg_ssg.toSSG(app, denoFileSystemModule, options);
};
//#endregion
exports.denoFileSystemModule = denoFileSystemModule;
exports.toSSG = toSSG;
