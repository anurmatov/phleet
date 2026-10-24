Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/adapter/netlify/handler.ts
/**
* @deprecated `hono/netlify` will be removed in v5. Install `@hono/netlify` and import from there instead.
*/
const handle = (app) => {
	return (req, context) => {
		return app.fetch(req, { context });
	};
};
//#endregion
exports.handle = handle;
