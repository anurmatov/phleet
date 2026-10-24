import { sha256 } from "../../utils/crypto.js";
import { cloneRawRequest } from "../../request.js";
//#region src/middleware/cache/index.ts
/**
* status codes that can be cached by default.
*/
const defaultCacheableStatusCodes = [200];
const defaultMaxQueryBodySize = 65536;
const cacheKeyPath = "/.hono/cache";
const cacheKeyParameter = "__hono_cache_key";
const cacheMethodKeyParameter = "__hono_cache_method";
const queryDigestKeyParameter = "__hono_query_digest";
const cacheVaryKeyParameter = "__hono_cache_vary";
const queryRepresentationMetadataHeaders = [
	"content-type",
	"content-encoding",
	"content-language",
	"content-location"
];
const shouldSkipCacheControl = (cacheControl) => !!cacheControl && /(?:^|,\s*)(?:private|no-(?:store|cache))(?:\s*(?:=|,|$))/i.test(cacheControl);
const parseVaryDirectives = (vary) => {
	if (vary == null) return [];
	return (Array.isArray(vary) ? vary : vary.split(",")).map((directive) => directive.trim().toLowerCase()).filter(Boolean);
};
const createCacheKey = (key, requestUrl, request, varyHeaders) => {
	const url = new URL(cacheKeyPath, requestUrl);
	url.searchParams.append(cacheKeyParameter, key);
	url.searchParams.append(cacheMethodKeyParameter, request.method);
	if (request.method === "QUERY") url.searchParams.append(queryDigestKeyParameter, request.digest);
	for (const header of varyHeaders) url.searchParams.append(cacheVaryKeyParameter, JSON.stringify(header));
	return url.href;
};
const shouldSkipCache = (res, optionsVaryDirectives, responseVary) => responseVary.length && (!optionsVaryDirectives || responseVary.some((name) => !optionsVaryDirectives.has(name))) || shouldSkipCacheControl(res.headers.get("Cache-Control")) || res.headers.has("Set-Cookie");
const reportCacheNotAvailable = (onCacheNotAvailable, reason) => {
	if (onCacheNotAvailable === false) {} else if (onCacheNotAvailable) onCacheNotAvailable(reason);
	else console.log(reason);
};
const createQueryDigest = async (c, maxQueryBodySize) => {
	if (!globalThis.crypto?.subtle) return;
	if (c.req.raw.bodyUsed && Object.keys(c.req.bodyCache)[0] === "formData") return;
	try {
		const requestHeaders = c.req.raw.headers;
		const metadata = new TextEncoder().encode(JSON.stringify(queryRepresentationMetadataHeaders.map((header) => [header, requestHeaders.get(header)])));
		const body = (await cloneRawRequest(c.req)).body;
		const chunks = [];
		let bodySize = 0;
		if (body) {
			const reader = body.getReader();
			for (;;) {
				const { done, value } = await reader.read();
				if (done) break;
				bodySize += value.byteLength;
				if (bodySize > maxQueryBodySize) {
					reader.cancel().catch(() => {});
					return;
				}
				chunks.push(value);
			}
		}
		const data = new Uint8Array(metadata.byteLength + bodySize);
		data.set(metadata);
		let offset = metadata.byteLength;
		for (const chunk of chunks) {
			data.set(chunk, offset);
			offset += chunk.byteLength;
		}
		return await sha256(data) ?? void 0;
	} catch {
		return;
	}
};
/**
* Cache Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/cache}
*
* @param {Object} options - The options for the cache middleware.
* @param {string | Function} options.cacheName - The name of the cache. Can be used to store multiple caches with different identifiers.
* @param {boolean} [options.wait=false] - A boolean indicating if Hono should wait for the Promise of the `cache.put` function to resolve before continuing with the request. Required to be true for the Deno environment.
* @param {string} [options.cacheControl] - A string of directives for the `Cache-Control` header.
* @param {string | string[]} [options.vary] - Adds the configured request headers to the cache key variants and sets the `Vary` header in the response. If the original response header already contains a `Vary` header, the values are merged, removing any duplicates.
* @param {Function} [options.keyGenerator] - Generates keys for every request in the `cacheName` store. This can be used to cache data based on request parameters or context parameters. QUERY keys additionally include a digest of the request content and its representation metadata.
* @param {number} [options.maxQueryBodySize=65536] - The maximum QUERY request body size in bytes that can be cached. Larger QUERY requests bypass the cache.
* @param {number[]} [options.cacheableStatusCodes=[200]] - An array of status codes that can be cached.
* @param {Function | false} [options.onCacheNotAvailable] - A callback invoked with the reason when `globalThis.caches` is not available or QUERY caching cannot use Web Crypto. By default, the reason is logged to the console. Set to `false` to suppress the log, or provide a custom function.
* @returns {MiddlewareHandler} The middleware handler function.
* @throws {Error} If the `vary` option includes "*".
*
* @example
* ```ts
* app.use(
*   '*',
*   cache({
*     cacheName: 'my-app',
*     cacheControl: 'max-age=3600',
*   })
* )
* ```
*/
const cache = (options) => {
	if (!globalThis.caches) {
		reportCacheNotAvailable(options.onCacheNotAvailable, "Cache Middleware is not enabled because caches is not defined.");
		return async (_c, next) => await next();
	}
	if (!globalThis.crypto?.subtle) reportCacheNotAvailable(options.onCacheNotAvailable, "Cache Middleware cannot cache QUERY requests because Web Crypto is not available.");
	if (options.wait === void 0) options.wait = false;
	const cacheControlDirectives = options.cacheControl?.split(",").map((directive) => directive.toLowerCase());
	const optionsVaryList = parseVaryDirectives(options.vary);
	const varyDirectives = optionsVaryList.length ? new Set(optionsVaryList) : void 0;
	if (varyDirectives?.has("*")) throw new Error("Middleware vary configuration cannot include \"*\", as it disallows effective caching.");
	const cacheableStatusCodes = new Set(options.cacheableStatusCodes ?? defaultCacheableStatusCodes);
	const maxQueryBodySize = options.maxQueryBodySize ?? defaultMaxQueryBodySize;
	const addHeader = (c, responseVary) => {
		if (cacheControlDirectives) {
			const existingDirectives = c.res.headers.get("Cache-Control")?.split(",").map((d) => d.trim().split("=", 1)[0].toLowerCase()) ?? [];
			for (const directive of cacheControlDirectives) {
				let [name, value] = directive.trim().split("=", 2);
				name = name.toLowerCase();
				if (!existingDirectives.includes(name)) c.header("Cache-Control", `${name}${value ? `=${value}` : ""}`, { append: true });
			}
		}
		if (varyDirectives) {
			if (responseVary.length === 0) c.header("Vary", Array.from(varyDirectives).join(", "));
			else {
				const merged = new Set(varyDirectives);
				for (const directive of responseVary) merged.add(directive);
				if (merged.has("*")) c.header("Vary", "*");
				else c.header("Vary", Array.from(merged).join(", "));
			}
		}
	};
	return async function cache(c, next) {
		if (c.req.method !== "GET" && c.req.method !== "QUERY" || c.req.raw.headers.has("Authorization")) {
			await next();
			return;
		}
		let cacheKeyRequest = { method: "GET" };
		if (c.req.method === "QUERY") {
			const digest = await createQueryDigest(c, maxQueryBodySize);
			if (digest === void 0) {
				await next();
				return;
			}
			cacheKeyRequest = {
				method: "QUERY",
				digest
			};
		}
		let key = c.req.url;
		if (options.keyGenerator) key = await options.keyGenerator(c);
		const varyHeaders = [];
		if (varyDirectives) for (const directive of varyDirectives) {
			const value = c.req.raw.headers.get(directive) ?? "";
			varyHeaders.push([directive, value]);
		}
		key = createCacheKey(key, c.req.url, cacheKeyRequest, varyHeaders);
		const cacheName = typeof options.cacheName === "function" ? await options.cacheName(c) : options.cacheName;
		const cache = await caches.open(cacheName);
		const response = await cache.match(key);
		if (response) return new Response(response.body, response);
		await next();
		if (!cacheableStatusCodes.has(c.res.status)) return;
		const responseVary = parseVaryDirectives(c.res.headers.get("Vary"));
		addHeader(c, responseVary);
		if (shouldSkipCache(c.res, varyDirectives, responseVary)) return;
		const res = c.res.clone();
		if (options.wait) await cache.put(key, res);
		else c.executionCtx.waitUntil(cache.put(key, res));
	};
};
//#endregion
export { cache };
