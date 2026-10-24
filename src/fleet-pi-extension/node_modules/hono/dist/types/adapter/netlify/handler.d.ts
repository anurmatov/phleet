import { Hono } from "../../hono.js";
//#region src/adapter/netlify/handler.d.ts
/**
 * @deprecated `hono/netlify` will be removed in v5. Install `@hono/netlify` and import from there instead.
 */
export declare const handle: (app: Hono<any, any>) => ((req: Request, context: any) => Response | Promise<Response>);
//#endregion