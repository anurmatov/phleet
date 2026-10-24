/** Default upper bound, in bytes, on a request body read by the HTTP entry points (4 MiB). */
export declare const DEFAULT_MAX_REQUEST_BODY_SIZE: number;
/** Upper bound on the number of messages accepted in one JSON-RPC batch array. */
export declare const MAX_BATCH_SIZE = 100;
/** The message answered with 413 for a request body over `maxBytes`. */
export declare function requestBodyTooLargeMessage(maxBytes: number): string;
/**
 * Resolves a `maxRequestBodySize` option to the bound to apply: the default when
 * omitted, otherwise the value itself, which must be a positive finite number of
 * bytes (a `RangeError` is thrown at configuration time for anything else).
 */
export declare function resolveMaxRequestBodySize(value: number | undefined): number;
/**
 * Reads a request body as text, up to `maxBytes` (default
 * {@linkcode DEFAULT_MAX_REQUEST_BODY_SIZE}). A declared `Content-Length` over the
 * limit is refused without reading anything; otherwise the read stops as soon as
 * more than the limit has arrived. Stream failures propagate.
 */
export declare function readRequestBody(request: Request, maxBytes?: number): Promise<{
    tooLarge: true;
} | {
    tooLarge: false;
    text: string;
}>;
//# sourceMappingURL=requestBody.d.ts.map