import { MiddlewareHandler } from "../../types.js";
import { Context } from "../../context.js";
import { HTTPException } from "../../http-exception.js";
//#region src/middleware/timeout/index.d.ts
export type HTTPExceptionFunction = (context: Context) => HTTPException;
/**
 * Timeout Middleware for Hono.
 *
 * @param {number} duration - The timeout duration in milliseconds.
 * @param {HTTPExceptionFunction | HTTPException} [exception=defaultTimeoutException] - The exception to throw when the timeout occurs. Can be a function that returns an HTTPException or an HTTPException object.
 * @returns {MiddlewareHandler} The middleware handler function.
 *
 * @example
 * ```ts
 * const app = new Hono()
 *
 * app.use(
 *   '/long-request',
 *   timeout(5000) // Set timeout to 5 seconds
 * )
 *
 * app.get('/long-request', async (c) => {
 *   await someLongRunningFunction()
 *   return c.text('Completed within time limit')
 * })
 * ```
 */
export declare const timeout: (duration: number, exception?: HTTPExceptionFunction | HTTPException) => MiddlewareHandler;
//#endregion