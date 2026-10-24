import { UnionToIntersection } from "../utils/types.js";
import { Hono } from "../hono.js";
import { Client, ClientRequestOptions } from "./types.js";
//#region src/client/client.d.ts
export declare const hc: <T extends Hono<any, any, any>, Prefix extends string = string>(baseUrl: Prefix, options?: ClientRequestOptions) => UnionToIntersection<Client<T, Prefix>>;
//#endregion