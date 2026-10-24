import { Hono } from "../../hono.js";
//#region src/adapter/vercel/handler.d.ts
/**
 * @deprecated `hono/vercel` will be removed in v5. Install `@hono/vercel` and import from there instead.
 */
export declare const handle: (app: Hono<any, any, any>) => (req: Request) => Response | Promise<Response>;
//#endregion