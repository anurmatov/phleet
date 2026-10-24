import { Env, Schema } from "../../types.js";
import { Hono } from "../../hono.js";
import { HandleOptions, handle } from "./handler.js";
//#region src/adapter/service-worker/index.d.ts
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
declare const fire: <E extends Env, S extends Schema, BasePath extends string>(app: Hono<E, S, BasePath>, options?: HandleOptions) => void;
//#endregion
export { fire, handle };
export {};
