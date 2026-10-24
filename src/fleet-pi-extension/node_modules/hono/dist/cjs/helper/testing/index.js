Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_client_client = require("../../client/client.js");
//#region src/helper/testing/index.ts
/**
* @module
* Testing Helper for Hono.
*/
const testClient = (app, Env, executionCtx, options) => {
	const customFetch = (input, init) => {
		return app.request(input, init, Env, executionCtx);
	};
	return require_client_client.hc("http://localhost", {
		...options,
		fetch: customFetch
	});
};
//#endregion
exports.testClient = testClient;
