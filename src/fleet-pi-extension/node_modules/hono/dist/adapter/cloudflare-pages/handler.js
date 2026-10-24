import { HTTPException } from "../../http-exception.js";
import { Context } from "../../context.js";
//#region src/adapter/cloudflare-pages/handler.ts
/**
* @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
*/
const handle = (app) => (eventContext) => {
	return app.fetch(eventContext.request, {
		...eventContext.env,
		eventContext
	}, {
		waitUntil: eventContext.waitUntil,
		passThroughOnException: eventContext.passThroughOnException,
		props: {}
	});
};
/**
* @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
*/
function handleMiddleware(middleware) {
	return async (executionCtx) => {
		const context = new Context(executionCtx.request, {
			env: {
				...executionCtx.env,
				eventContext: executionCtx
			},
			executionCtx
		});
		let response = void 0;
		try {
			response = await middleware(context, async () => {
				try {
					context.res = await executionCtx.next();
				} catch (error) {
					if (error instanceof Error) context.error = error;
					else throw error;
				}
			});
		} catch (error) {
			if (error instanceof Error) context.error = error;
			else throw error;
		}
		if (response) return response;
		if (context.error instanceof HTTPException) return context.error.getResponse();
		if (context.error) throw context.error;
		return context.res;
	};
}
/**
*
* @description `serveStatic()` is for advanced mode:
* https://developers.cloudflare.com/pages/platform/functions/advanced-mode/#set-up-a-function
*
* @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
*/
const serveStatic = () => {
	return async (c) => {
		const res = await c.env.ASSETS.fetch(c.req.raw);
		if (res.status === 404) return c.notFound();
		return res;
	};
};
//#endregion
export { handle, handleMiddleware, serveStatic };
