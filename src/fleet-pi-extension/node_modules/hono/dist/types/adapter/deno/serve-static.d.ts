import { Env, MiddlewareHandler } from "../../types.js";
import { ServeStaticOptions } from "../../middleware/serve-static/index.js";
//#region src/adapter/deno/serve-static.d.ts
/**
 * @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
 */
export declare const serveStatic: <E extends Env = Env>(options?: ServeStaticOptions<E>) => MiddlewareHandler;
//#endregion