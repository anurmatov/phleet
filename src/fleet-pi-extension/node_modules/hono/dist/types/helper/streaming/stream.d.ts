import { Context } from "../../context.js";
import { StreamingApi } from "../../utils/stream.js";
//#region src/helper/streaming/stream.d.ts
export declare const stream: (c: Context, cb: (stream: StreamingApi) => Promise<void>, onError?: (e: Error, stream: StreamingApi) => Promise<void>) => Response;
//#endregion