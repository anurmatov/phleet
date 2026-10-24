import { Env, Schema } from "../../types.js";
import { Hono } from "../../hono.js";
import { FetchEvent } from "./types.js";
//#region src/adapter/service-worker/handler.d.ts
type Handler = (evt: FetchEvent) => void;
/**
 * @deprecated `hono/service-worker` will be removed in v5. Install `@hono/service-worker` and import from there instead.
 */
export type HandleOptions = {
  fetch?: typeof fetch;
};
/**
 * Adapter for Service Worker
 * @deprecated `hono/service-worker` will be removed in v5. Install `@hono/service-worker` and import from there instead.
 */
export declare const handle: <E extends Env, S extends Schema, BasePath extends string>(app: Hono<E, S, BasePath>, opts?: HandleOptions) => Handler;
//#endregion
export {};
