import { decodeBase64, encodeBase64 } from "../../utils/encode.js";
import crypto from "node:crypto";
//#region src/adapter/lambda-edge/handler.ts
globalThis.crypto ??= crypto;
/**
* Accepts events from 'Lambda@Edge' event
* https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/lambda-event-structure.html
*/
const convertHeaders = (headers) => {
	const cfHeaders = {};
	headers.forEach((value, key) => {
		cfHeaders[key.toLowerCase()] = [...cfHeaders[key.toLowerCase()] || [], {
			key: key.toLowerCase(),
			value
		}];
	});
	return cfHeaders;
};
/**
* @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
*/
const handle = (app) => {
	return async (event, ...args) => {
		const [context, callback] = args;
		let callbackError = null;
		let callbackResult;
		const cf = getCloudFrontRecord(event);
		const res = await app.fetch(createRequest(cf), {
			event,
			context,
			callback: (err, result) => {
				if (!callbackError && !callbackResult) {
					callbackError = err;
					callbackResult = result;
				}
				callback?.(err, result);
			},
			config: cf.config,
			request: cf.request,
			response: cf.response
		});
		if (callbackError) throw callbackError;
		return callbackResult ?? createResult(res);
	};
};
const createResult = async (res) => {
	const contentEncoding = res.headers.get("content-encoding");
	const isBase64Encoded = isContentTypeBinary(res.headers.get("content-type") || "") || !!contentEncoding && !/^identity$/i.test(contentEncoding);
	const body = isBase64Encoded ? encodeBase64(await res.arrayBuffer()) : await res.text();
	return {
		status: res.status.toString(),
		headers: convertHeaders(res.headers),
		body,
		...isBase64Encoded && { bodyEncoding: "base64" }
	};
};
/**
* Reads the CloudFront record out of a Lambda@Edge event.
*
* The event is supplied by the runtime, so a malformed one means the function
* was invoked with something other than a Lambda@Edge event. Fail with a
* message naming the adapter and the missing field, rather than letting an
* unattributable property access error escape.
*/
const getCloudFrontRecord = (event) => {
	const cf = event?.Records?.[0]?.cf;
	if (!cf?.request) throw new TypeError("Unable to map the CloudFront event to a Request: expected `Records[0].cf.request` in the Lambda@Edge event.");
	return cf;
};
const createRequest = (cf) => {
	const request = cf.request;
	const queryString = request.querystring;
	const urlPath = `https://${request.headers?.host?.[0]?.value || cf.config?.distributionDomainName}${request.uri}`;
	const url = queryString ? `${urlPath}?${queryString}` : urlPath;
	const headers = new Headers();
	Object.entries(request.headers ?? {}).forEach(([k, v]) => {
		v.forEach((header) => headers.append(k, header.value));
	});
	const requestBody = request.body;
	const method = request.method;
	const rawBody = createBody(method, requestBody);
	let body = rawBody;
	if (rawBody !== void 0) {
		const bytes = typeof rawBody === "string" ? new TextEncoder().encode(rawBody) : rawBody;
		body = bytes;
		headers.set("content-length", bytes.length.toString());
	}
	return new Request(url, {
		headers,
		method,
		body
	});
};
/**
* @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
*/
const createBody = (method, requestBody) => {
	if (!requestBody || !requestBody.data) return;
	if (method === "GET" || method === "HEAD") return;
	if (requestBody.encoding === "base64") return decodeBase64(requestBody.data);
	return requestBody.data;
};
/**
* @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
*/
const isContentTypeBinary = (contentType) => {
	if (/^application\/vnd\.(?:apple\.installer|mozilla\.xul)\+xml\s*(?:;|$)/i.test(contentType)) return true;
	return !/^text\/(?:plain|html|css|javascript|csv)|(?:\/|\+)(?:json|xml)\s*(?:;|$)/i.test(contentType);
};
//#endregion
export { createBody, handle, isContentTypeBinary };
