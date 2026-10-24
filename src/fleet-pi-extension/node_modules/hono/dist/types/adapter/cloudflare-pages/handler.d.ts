import { BlankSchema, Env, Input, MiddlewareHandler, Schema } from "../../types.js";
import { Hono } from "../../hono.js";
//#region src/adapter/cloudflare-pages/handler.d.ts
type Params<P extends string = any> = Record<P, string | string[]>;
/**
 * @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
 */
export type EventContext<Env = {}, P extends string = any, Data = Record<string, unknown>> = {
  request: Request;
  functionPath: string;
  waitUntil: (promise: Promise<unknown>) => void;
  passThroughOnException: () => void;
  props: any;
  next: (input?: Request | string, init?: RequestInit) => Promise<Response>;
  env: Env & {
    ASSETS: {
      fetch: typeof fetch;
    };
  };
  params: Params<P>;
  data: Data;
};
declare type PagesFunction<Env = unknown, Params extends string = any, Data extends Record<string, unknown> = Record<string, unknown>> = (context: EventContext<Env, Params, Data>) => Response | Promise<Response>;
/**
 * @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
 */
export declare const handle: <E extends Env = Env, S extends Schema = BlankSchema, BasePath extends string = "/">(app: Hono<E, S, BasePath>) => PagesFunction<E["Bindings"]>;
/**
 * @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
 */
export declare function handleMiddleware<E extends Env = {}, P extends string = any, I extends Input = {}>(middleware: MiddlewareHandler<E & {
  Bindings: {
    eventContext: EventContext;
  };
}, P, I>): PagesFunction<E['Bindings']>;
/**
 *
 * @description `serveStatic()` is for advanced mode:
 * https://developers.cloudflare.com/pages/platform/functions/advanced-mode/#set-up-a-function
 *
 * @deprecated `hono/cloudflare-pages` will be removed in v5. Cloudflare recommends Workers with static assets; use `hono` on Workers instead.
 */
export declare const serveStatic: () => MiddlewareHandler;
//#endregion
export {};
