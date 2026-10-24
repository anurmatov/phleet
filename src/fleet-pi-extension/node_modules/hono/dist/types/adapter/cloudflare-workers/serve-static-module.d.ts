import { Env, MiddlewareHandler } from "../../types.js";
import { ServeStaticOptions } from "./serve-static.js";
//#region src/adapter/cloudflare-workers/serve-static-module.d.ts
declare const module: <E extends Env = Env>(options: Omit<ServeStaticOptions<E>, "namespace">) => MiddlewareHandler;
//#endregion
export { module as serveStatic };
export {};
