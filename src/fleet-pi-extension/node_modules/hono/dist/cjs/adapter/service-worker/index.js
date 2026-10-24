Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_service_worker_handler = require("./handler.js");
//#region src/adapter/service-worker/index.ts
/**
* Registers a Hono app to handle fetch events in a service worker.
* This sets up `addEventListener('fetch', handle(app, options))` for the provided app.
*
* @param app - The Hono application instance
* @param options - Options for handling requests (fetch defaults to undefined)
* @example
* ```ts
* import { Hono } from 'hono'
* import { fire } from 'hono/service-worker'
*
* const app = new Hono()
*
* app.get('/', (c) => c.text('Hi'))
*
* fire(app)
* ```
*/
const fire = (app, options) => {
	addEventListener("fetch", require_adapter_service_worker_handler.handle(app, options));
};
//#endregion
exports.fire = fire;
exports.handle = require_adapter_service_worker_handler.handle;
