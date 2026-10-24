import { MiddlewareHandler } from "../../types.js";
import { Context } from "../../context.js";
//#region src/middleware/mount/index.d.ts
type ApplicationHandler = (request: Request, ...args: any) => Response | Promise<Response>;
type MountOptionHandler = (c: Context) => unknown;
type MountReplaceRequest = (originalRequest: Request) => Request;
type MountOptions = MountOptionHandler | {
  optionHandler?: MountOptionHandler;
  replaceRequest?: MountReplaceRequest | false;
};
/**
 * `mount()` allows you to mount applications built with other frameworks into your Hono application.
 *
 * @see {@link https://hono.dev/docs/api/hono#mount}
 *
 * @param {Function} applicationHandler - other Request Handler
 * @param {MountOptions} [options] - options of `mount()`
 * @returns {MiddlewareHandler} handler to register with `app.all()`
 *
 * @example
 * ```ts
 * import { Router as IttyRouter } from 'itty-router'
 * import { Hono } from 'hono'
 * import { mount } from 'hono/mount'
 * // Create itty-router application
 * const ittyRouter = IttyRouter()
 * // GET /itty-router/hello
 * ittyRouter.get('/hello', () => new Response('Hello from itty-router'))
 *
 * const app = new Hono()
 * app.all('/itty-router/*', mount(ittyRouter.handle))
 * ```
 *
 * @example
 * ```ts
 * const app = new Hono()
 * // Send the request to another application without modification.
 * app.all('/app/*', mount(anotherApp, {
 *   replaceRequest: (req) => req,
 * }))
 * ```
 */
export declare const mount: (applicationHandler: ApplicationHandler, options?: MountOptions) => MiddlewareHandler;
//#endregion
export type { ApplicationHandler, MountOptionHandler, MountOptions, MountReplaceRequest };
export {};
