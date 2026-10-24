import { HTTPException } from "../../http-exception.js";
//#region src/helper/proxy/index.ts
/**
* @module
* Proxy Helper for Hono.
*/
const hopByHopHeaders = [
	"connection",
	"keep-alive",
	"proxy-authenticate",
	"proxy-authorization",
	"te",
	"trailer",
	"transfer-encoding",
	"upgrade"
];
const ALLOWED_TOKEN_PATTERN = /^[!#$%&'*+\-.0-9A-Z^_`a-z|~]+$/;
const buildRequestInitFromRequest = (request, strictConnectionProcessing) => {
	if (!request) return {};
	const headers = new Headers(request.headers);
	if (strictConnectionProcessing) {
		const connectionValue = headers.get("connection");
		if (connectionValue) {
			const headerNames = connectionValue.split(",").map((h) => h.trim());
			const invalidHeaders = headerNames.filter((h) => !ALLOWED_TOKEN_PATTERN.test(h));
			if (invalidHeaders.length > 0) throw new HTTPException(400, { message: `Invalid Connection header value: ${invalidHeaders.join(", ")}` });
			headerNames.forEach((headerName) => {
				headers.delete(headerName);
			});
		}
	}
	hopByHopHeaders.forEach((header) => {
		headers.delete(header);
	});
	return {
		method: request.method,
		body: request.body,
		duplex: request.body ? "half" : void 0,
		headers,
		signal: request.signal
	};
};
const preprocessRequestInit = (requestInit) => {
	if (!requestInit.headers || Array.isArray(requestInit.headers) || requestInit.headers instanceof Headers) return requestInit;
	const headers = new Headers();
	for (const [key, value] of Object.entries(requestInit.headers)) if (value == null) headers.delete(key);
	else headers.set(key, value);
	requestInit.headers = headers;
	return requestInit;
};
/**
* Fetch API wrapper for proxy.
* The parameters and return value are the same as for `fetch` (except for the proxy-specific options).
*
* The "Accept-Encoding" header is replaced with an encoding that the current runtime can handle.
* Unnecessary response headers are deleted and a Response object is returned that can be returned
* as is as a response from the handler.
*
* @example
* ```ts
* app.get('/proxy/:path', (c) => {
*   return proxy(`http://${originServer}/${c.req.param('path')}`, {
*     headers: {
*       ...c.req.header(), // optional, specify only when forwarding all the request data (including credentials) is necessary.
*       'X-Forwarded-For': '127.0.0.1',
*       'X-Forwarded-Host': c.req.header('host'),
*       Authorization: undefined, // do not propagate request headers contained in c.req.header('Authorization')
*     },
*   }).then((res) => {
*     res.headers.delete('Set-Cookie')
*     return res
*   })
* })
*
* app.all('/proxy/:path', (c) => {
*   return proxy(`http://${originServer}/${c.req.param('path')}`, {
*     ...c.req, // optional, specify only when forwarding all the request data (including credentials) is necessary.
*     headers: {
*       ...c.req.header(),
*       'X-Forwarded-For': '127.0.0.1',
*       'X-Forwarded-Host': c.req.header('host'),
*       Authorization: undefined, // do not propagate request headers contained in c.req.header('Authorization')
*     },
*   })
* })
*
* // Strict RFC compliance mode (use only in trusted environments)
* app.get('/internal-proxy/:path', (c) => {
*   return proxy(`http://${internalServer}/${c.req.param('path')}`, {
*     ...c.req,
*     strictConnectionProcessing: true,
*   })
* })
* ```
*/
const proxy = async (input, proxyInit) => {
	const { raw, customFetch, strictConnectionProcessing = false, ...requestInit } = proxyInit instanceof Request ? { raw: proxyInit } : proxyInit ?? {};
	const req = new Request(input, {
		...buildRequestInitFromRequest(raw, strictConnectionProcessing),
		...preprocessRequestInit(requestInit)
	});
	req.headers.delete("accept-encoding");
	const res = await (customFetch || fetch)(req);
	const resHeaders = new Headers(res.headers);
	const connectionValue = resHeaders.get("connection");
	if (connectionValue) connectionValue.split(",").map((h) => h.trim()).filter((h) => ALLOWED_TOKEN_PATTERN.test(h)).forEach((h) => resHeaders.delete(h));
	hopByHopHeaders.forEach((header) => {
		resHeaders.delete(header);
	});
	if (resHeaders.has("content-encoding")) {
		resHeaders.delete("content-encoding");
		resHeaders.delete("content-length");
	}
	return new Response(res.body, {
		status: res.status,
		statusText: res.statusText,
		headers: resHeaders
	});
};
//#endregion
export { proxy };
