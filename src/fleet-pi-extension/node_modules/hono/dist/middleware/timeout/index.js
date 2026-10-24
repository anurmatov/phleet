import { HTTPException } from "../../http-exception.js";
//#region src/middleware/timeout/index.ts
const defaultTimeoutException = new HTTPException(504, { message: "Gateway Timeout" });
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
const timeout = (duration, exception = defaultTimeoutException) => {
	return async function timeout(context, next) {
		let timer;
		const timeoutPromise = new Promise((_, reject) => {
			timer = setTimeout(() => {
				reject(typeof exception === "function" ? exception(context) : exception);
			}, duration);
		});
		try {
			await Promise.race([next(), timeoutPromise]);
		} finally {
			if (timer !== void 0) clearTimeout(timer);
		}
	};
};
//#endregion
export { timeout };
