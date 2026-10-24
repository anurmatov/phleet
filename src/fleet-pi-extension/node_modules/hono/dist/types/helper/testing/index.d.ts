import { UnionToIntersection } from "../../utils/types.js";
import { Schema } from "../../types.js";
import { ExecutionContext } from "../../context.js";
import { Hono } from "../../hono.js";
import { Client, ClientRequestOptions } from "../../client/types.js";
//#region src/helper/testing/index.d.ts
type ExtractEnv<T> = T extends Hono<infer E, Schema, string> ? E : never;
export declare const testClient: <T extends Hono<any, Schema, string>>(app: T, Env?: ExtractEnv<T>["Bindings"] | {}, executionCtx?: ExecutionContext, options?: Omit<ClientRequestOptions, "fetch">) => UnionToIntersection<Client<T, "http://localhost">>;
//#endregion
export {};
