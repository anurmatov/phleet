import { hc } from "../../client/client.js";
//#region src/helper/testing/index.ts
/**
* @module
* Testing Helper for Hono.
*/
const testClient = (app, Env, executionCtx, options) => {
	const customFetch = (input, init) => {
		return app.request(input, init, Env, executionCtx);
	};
	return hc("http://localhost", {
		...options,
		fetch: customFetch
	});
};
//#endregion
export { testClient };
