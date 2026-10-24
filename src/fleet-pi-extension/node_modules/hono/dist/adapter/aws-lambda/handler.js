import { decodeBase64, encodeBase64 } from "../../utils/encode.js";
import { pipeline } from "node:stream/promises";
//#region src/adapter/aws-lambda/handler.ts
function sanitizeHeaderValue(value) {
	if (!/[^\x00-\x7F]/.test(value)) return value;
	return encodeURIComponent(value);
}
const getRequestContext = (event) => {
	return event.requestContext;
};
async function* readWebStream(reader) {
	let readResult = await reader.read();
	while (!readResult.done) {
		yield readResult.value;
		readResult = await reader.read();
	}
}
const streamToNodeStream = (reader, writer) => pipeline(readWebStream(reader), writer);
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
const streamHandle = (app) => {
	return awslambda.streamifyResponse(async (event, responseStream, context) => {
		const processor = getProcessor(event);
		try {
			const req = processor.createRequest(event);
			const requestContext = getRequestContext(event);
			const res = await app.fetch(req, {
				event,
				requestContext,
				context
			});
			const headers = {};
			const cookies = [];
			res.headers.forEach((value, name) => {
				if (name === "set-cookie") cookies.push(value);
				else headers[name] = value;
			});
			const httpResponseMetadata = {
				statusCode: res.status,
				headers,
				cookies
			};
			responseStream = awslambda.HttpResponseStream.from(responseStream, httpResponseMetadata);
			if (res.body) await streamToNodeStream(res.body.getReader(), responseStream);
			else responseStream.write("");
		} catch (error) {
			console.error("Error processing request:", error);
			responseStream.write("Internal Server Error");
		} finally {
			responseStream.end();
		}
	});
};
/**
* Converts a Hono application to an AWS Lambda handler.
*
* Accepts events from API Gateway (v1 and v2), Application Load Balancer (ALB),
* and Lambda Function URLs.
*
* @param app - The Hono application instance
* @param options - Optional configuration
* @param options.isContentTypeBinary - A function to determine if the content type is binary.
*                                      If not provided, the default function will be used.
* @returns Lambda handler function
*
* @example
* ```js
* import { Hono } from 'hono'
* import { handle } from 'hono/aws-lambda'
*
* const app = new Hono()
*
* app.get('/', (c) => c.text('Hello from Lambda'))
* app.get('/json', (c) => c.json({ message: 'Hello JSON' }))
*
* export const handler = handle(app)
* ```
*
* @example
* ```js
* // With custom binary content type detection
* import { handle, defaultIsContentTypeBinary } from 'hono/aws-lambda'
* export const handler = handle(app, {
*   isContentTypeBinary: (contentType) => {
*     if (defaultIsContentTypeBinary(contentType)) {
*       // default logic same as prior to v4.8.4
*       return true
*     }
*     return contentType.startsWith('image/') || contentType === 'application/pdf'
*   }
* })
* ```
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
const handle = (app, { isContentTypeBinary } = { isContentTypeBinary: void 0 }) => {
	return async (event, lambdaContext) => {
		const processor = getProcessor(event);
		let req, requestContext;
		try {
			req = processor.createRequest(event);
			requestContext = getRequestContext(event);
		} catch (error) {
			console.error("Error processing request:", error);
			const errorResponse = error instanceof TypeError ? new Response("Invalid request", { status: 400 }) : new Response("Internal Server Error", { status: 500 });
			return processor.createResult(event, errorResponse, { isContentTypeBinary });
		}
		const res = await app.fetch(req, {
			event,
			requestContext,
			lambdaContext
		});
		return processor.createResult(event, res, { isContentTypeBinary });
	};
};
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
var EventProcessor = class {
	getHeaderValue(headers, key) {
		return headers ? Array.isArray(headers[key]) ? headers[key][0] : headers[key] : void 0;
	}
	getDomainName(event) {
		if (event.requestContext && "domainName" in event.requestContext) return event.requestContext.domainName;
		const hostFromHeaders = this.getHeaderValue(event.headers, "host");
		if (hostFromHeaders) return hostFromHeaders;
		const multiValueHeaders = "multiValueHeaders" in event ? event.multiValueHeaders : {};
		return this.getHeaderValue(multiValueHeaders, "host");
	}
	createRequest(event) {
		const queryString = this.getQueryString(event);
		const urlPath = `https://${this.getDomainName(event)}${this.getPath(event)}`;
		const url = queryString ? `${urlPath}?${queryString}` : urlPath;
		const headers = this.getHeaders(event);
		const requestInit = {
			headers,
			method: this.getMethod(event)
		};
		if (event.body) {
			const body = event.isBase64Encoded ? decodeBase64(event.body) : new TextEncoder().encode(event.body);
			requestInit.body = body;
			headers.set("content-length", body.length.toString());
		}
		return new Request(url, requestInit);
	}
	async createResult(event, res, options) {
		const contentType = res.headers.get("content-type");
		const isContentTypeBinary = options.isContentTypeBinary ?? defaultIsContentTypeBinary;
		let isBase64Encoded = contentType && isContentTypeBinary(contentType) ? true : false;
		if (!isBase64Encoded) {
			const contentEncoding = res.headers.get("content-encoding");
			isBase64Encoded = isContentEncodingBinary(contentEncoding);
		}
		const result = {
			body: isBase64Encoded ? encodeBase64(await res.arrayBuffer()) : await res.text(),
			statusCode: res.status,
			isBase64Encoded,
			..."multiValueHeaders" in event && event.multiValueHeaders ? { multiValueHeaders: {} } : { headers: {} }
		};
		this.setCookies(event, res, result);
		if (result.multiValueHeaders) res.headers.forEach((value, key) => {
			result.multiValueHeaders[key] = [value];
		});
		else res.headers.forEach((value, key) => {
			result.headers[key] = value;
		});
		return result;
	}
	setCookies(_event, res, result) {
		if (res.headers.has("set-cookie")) {
			const cookies = res.headers.getSetCookie ? res.headers.getSetCookie() : Array.from(res.headers.entries()).filter(([k]) => k === "set-cookie").map(([, v]) => v);
			if (Array.isArray(cookies)) {
				this.setCookiesToResult(result, cookies);
				res.headers.delete("set-cookie");
			}
		}
	}
};
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
var EventV2Processor = class extends EventProcessor {
	getPath(event) {
		return event.rawPath;
	}
	getMethod(event) {
		return event.requestContext.http.method;
	}
	getQueryString(event) {
		return event.rawQueryString;
	}
	getCookies(event, headers) {
		if (Array.isArray(event.cookies)) headers.set("Cookie", event.cookies.join("; "));
	}
	setCookiesToResult(result, cookies) {
		result.cookies = cookies;
	}
	getHeaders(event) {
		const headers = new Headers();
		this.getCookies(event, headers);
		if (event.headers) {
			for (const [k, v] of Object.entries(event.headers)) if (v) headers.set(k, v);
		}
		return headers;
	}
};
const v2Processor = new EventV2Processor();
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
var EventV1Processor = class extends EventProcessor {
	getPath(event) {
		return event.path;
	}
	getMethod(event) {
		return event.httpMethod;
	}
	getQueryString(event) {
		if (event.multiValueQueryStringParameters) return Object.entries(event.multiValueQueryStringParameters || {}).filter(([, value]) => value).map(([key, values]) => values.map((value) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`).join("&")).join("&");
		else return Object.entries(event.queryStringParameters || {}).filter(([, value]) => value !== void 0).map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value || "")}`).join("&");
	}
	getCookies(_event, _headers) {}
	getHeaders(event) {
		const headers = new Headers();
		this.getCookies(event, headers);
		if (event.multiValueHeaders) {
			for (const [k, values] of Object.entries(event.multiValueHeaders)) if (values) values.forEach((v) => headers.append(k, sanitizeHeaderValue(v)));
		}
		if (event.headers) {
			for (const [k, v] of Object.entries(event.headers)) if (v && !headers.has(k)) headers.set(k, sanitizeHeaderValue(v));
		}
		return headers;
	}
	setCookiesToResult(result, cookies) {
		result.multiValueHeaders = { "set-cookie": cookies };
	}
};
const v1Processor = new EventV1Processor();
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
var ALBProcessor = class extends EventProcessor {
	getHeaders(event) {
		const headers = new Headers();
		if (event.multiValueHeaders) {
			for (const [key, values] of Object.entries(event.multiValueHeaders)) if (values && Array.isArray(values)) {
				const sanitizedValue = sanitizeHeaderValue(values.join("; "));
				headers.set(key, sanitizedValue);
			}
		} else for (const [key, value] of Object.entries(event.headers ?? {})) if (value) headers.set(key, sanitizeHeaderValue(value));
		return headers;
	}
	getPath(event) {
		return event.path;
	}
	getMethod(event) {
		return event.httpMethod;
	}
	getQueryString(event) {
		if (event.multiValueQueryStringParameters) return Object.entries(event.multiValueQueryStringParameters || {}).filter(([, value]) => value).map(([key, value]) => `${key}=${value.join(`&${key}=`)}`).join("&");
		else return Object.entries(event.queryStringParameters || {}).filter(([, value]) => value !== void 0).map(([key, value]) => `${key}=${value}`).join("&");
	}
	getCookies(event, headers) {
		let cookie;
		if (event.multiValueHeaders) cookie = event.multiValueHeaders["cookie"]?.join("; ");
		else cookie = event.headers ? event.headers["cookie"] : void 0;
		if (cookie) headers.append("Cookie", cookie);
	}
	setCookiesToResult(result, cookies) {
		if (result.multiValueHeaders) result.multiValueHeaders["set-cookie"] = cookies;
		else result.headers["set-cookie"] = cookies[0];
	}
};
const albProcessor = new ALBProcessor();
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
var LatticeV2Processor = class extends EventProcessor {
	getPath(event) {
		return event.path;
	}
	getMethod(event) {
		return event.method;
	}
	getQueryString() {
		return "";
	}
	getHeaders(event) {
		const headers = new Headers();
		if (event.headers) {
			for (const [k, values] of Object.entries(event.headers)) if (values) values.forEach((v) => headers.append(k, sanitizeHeaderValue(v)));
		}
		return headers;
	}
	getCookies() {}
	setCookiesToResult(result, cookies) {
		result.headers = {
			...result.headers,
			"set-cookie": cookies
		};
	}
};
const latticeV2Processor = new LatticeV2Processor();
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
const getProcessor = (event) => {
	if (isProxyEventALB(event)) return albProcessor;
	if (isProxyEventV2(event)) return v2Processor;
	if (isLatticeEventV2(event)) return latticeV2Processor;
	return v1Processor;
};
const isProxyEventALB = (event) => {
	if (event.requestContext) return Object.hasOwn(event.requestContext, "elb");
	return false;
};
const isProxyEventV2 = (event) => {
	return Object.hasOwn(event, "rawPath") && Object.hasOwn(event.requestContext ?? {}, "http");
};
const isLatticeEventV2 = (event) => {
	if (event.requestContext) return Object.hasOwn(event.requestContext, "serviceArn");
	return false;
};
/**
* Check if the given content type is binary.
* This is a default function and may be overwritten by the user via `isContentTypeBinary` option in handler().
* @param contentType The content type to check.
* @returns True if the content type is binary, false otherwise.
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
const defaultIsContentTypeBinary = (contentType) => {
	if (/^application\/vnd\.(?:apple\.installer|mozilla\.xul)\+xml\s*(?:;|$)/i.test(contentType)) return true;
	return !/^text\/(?:plain|html|css|javascript|csv)|(?:\/|\+)(?:json|xml)\s*(?:;|$)/i.test(contentType);
};
/**
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
const isContentEncodingBinary = (contentEncoding) => {
	return !!contentEncoding && !/^identity$/i.test(contentEncoding);
};
//#endregion
export { ALBProcessor, EventProcessor, EventV1Processor, EventV2Processor, LatticeV2Processor, defaultIsContentTypeBinary, getProcessor, handle, isContentEncodingBinary, streamHandle };
