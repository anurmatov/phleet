//#region src/adapter/cloudflare-workers/utils.d.ts
/**
 * @deprecated `hono/cloudflare-workers` will be removed in v5. Install `@hono/cloudflare-workers` and import from there instead.
 */
export type KVAssetOptions = {
  manifest?: object | string;
  namespace?: unknown;
};
/**
 * @deprecated `hono/cloudflare-workers` will be removed in v5. Install `@hono/cloudflare-workers` and import from there instead.
 */
export declare const getContentFromKVAsset: (path: string, options?: KVAssetOptions) => Promise<ReadableStream | null>;
//#endregion