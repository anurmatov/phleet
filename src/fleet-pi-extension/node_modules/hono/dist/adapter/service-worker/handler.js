//#region src/adapter/service-worker/handler.ts
/**
* Adapter for Service Worker
* @deprecated `hono/service-worker` will be removed in v5. Install `@hono/service-worker` and import from there instead.
*/
const handle = (app, opts = { fetch: globalThis.fetch.bind(globalThis) }) => {
	return (evt) => {
		evt.respondWith((async () => {
			const res = await app.fetch(evt.request, {}, evt);
			if (opts.fetch && res.status === 404) return await opts.fetch(evt.request);
			return res;
		})());
	};
};
//#endregion
export { handle };
