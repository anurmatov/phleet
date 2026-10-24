Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_helper_ssg_ssg = require("../../helper/ssg/ssg.js");
require("../../helper/ssg/index.js");
//#region src/adapter/bun/ssg.ts
const { write } = Bun;
/**
* @experimental
* `bunFileSystemModule` is an experimental feature.
* The API might be changed.
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const bunFileSystemModule = {
	writeFile: async (path, data) => {
		await write(path, data);
	},
	mkdir: async () => {}
};
/**
* @experimental
* `toSSG` is an experimental feature.
* The API might be changed.
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const toSSG = async (app, options) => {
	return require_helper_ssg_ssg.toSSG(app, bunFileSystemModule, options);
};
//#endregion
exports.bunFileSystemModule = bunFileSystemModule;
exports.toSSG = toSSG;
