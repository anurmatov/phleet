import { Env, MiddlewareHandler } from "../../types.js";
import { Context } from "../../context.js";
//#region src/middleware/context-storage/index.d.ts
/**
 * Context Storage Middleware for Hono.
 *
 * @see {@link https://hono.dev/docs/middleware/builtin/context-storage}
 *
 * @returns {MiddlewareHandler} The middleware handler function.
 *
 * @example
 * ```ts
 * type Env = {
 *   Variables: {
 *     message: string
 *   }
 * }
 *
 * const app = new Hono<Env>()
 *
 * app.use(contextStorage())
 *
 * app.use(async (c, next) => {
 *   c.set('message', 'Hono is hot!!')
 *   await next()
 * })
 *
 * app.get('/', async (c) => c.text(getMessage()))
 *
 * const getMessage = () => {
 *   return getContext<Env>().var.message
 * }
 * ```
 */
export declare const contextStorage: () => MiddlewareHandler;
export declare const tryGetContext: <E extends Env = Env>() => Context<E> | undefined;
export declare const getContext: <E extends Env = Env>() => Context<E>;
//#endregion