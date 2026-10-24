//#region src/adapter/vercel/handler.ts
/**
* @deprecated `hono/vercel` will be removed in v5. Install `@hono/vercel` and import from there instead.
*/
const handle = (app) => (req) => {
	return app.fetch(req);
};
//#endregion
export { handle };
