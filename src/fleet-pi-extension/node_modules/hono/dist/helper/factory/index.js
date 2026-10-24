import { Hono } from "../../hono.js";
//#region src/helper/factory/index.ts
/**
* @module
* Factory Helper for Hono.
*/
var Factory = class {
	initApp;
	#defaultAppOptions;
	constructor(init) {
		this.initApp = init?.initApp;
		this.#defaultAppOptions = init?.defaultAppOptions;
	}
	createApp = (options) => {
		const app = new Hono(options && this.#defaultAppOptions ? {
			...this.#defaultAppOptions,
			...options
		} : options ?? this.#defaultAppOptions);
		if (this.initApp) this.initApp(app);
		return app;
	};
	createMiddleware = (middleware) => middleware;
	createHandlers = (...handlers) => {
		return handlers.filter((handler) => handler !== void 0);
	};
};
const createFactory = (init) => new Factory(init);
const createMiddleware = (middleware) => middleware;
//#endregion
export { Factory, createFactory, createMiddleware };
