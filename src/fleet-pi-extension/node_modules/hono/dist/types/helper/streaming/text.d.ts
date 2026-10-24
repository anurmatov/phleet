import { Context } from "../../context.js";
import { StreamingApi } from "../../utils/stream.js";
//#region src/helper/streaming/text.d.ts
export declare const streamText: (c: Context, cb: (stream: StreamingApi) => Promise<void>, onError?: (e: Error, stream: StreamingApi) => Promise<void>) => Response;
//#endregion