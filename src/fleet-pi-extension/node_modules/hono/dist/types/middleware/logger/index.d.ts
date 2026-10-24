import { MiddlewareHandler } from "../../types.js";
//#region src/middleware/logger/index.d.ts
type PrintFunc = (str: string, ...rest: string[]) => void;
/**
 * Logger Middleware for Hono.
 *
 * @see {@link https://hono.dev/docs/middleware/builtin/logger}
 *
 * @param {PrintFunc} [fn=console.log] - Optional function for customized logging behavior.
 * @returns {MiddlewareHandler} The middleware handler function.
 *
 * @example
 * ```ts
 * const app = new Hono()
 *
 * app.use(logger())
 * app.get('/', (c) => c.text('Hello Hono!'))
 * ```
 */
export declare const logger: (fn?: PrintFunc) => MiddlewareHandler;
//#endregion
export {};
