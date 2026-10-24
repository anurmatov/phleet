Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/adapter/aws-lambda/conninfo.ts
/**
* Get connection information from AWS Lambda
*
* Extracts client IP from various Lambda event sources:
* - API Gateway v1 (REST API): requestContext.identity.sourceIp
* - API Gateway v2 (HTTP API/Function URLs): requestContext.http.sourceIp
* - ALB: Falls back to x-forwarded-for header
*
* @param c - Context
* @returns Connection information including remote address
* @example
* ```ts
* import { Hono } from 'hono'
* import { handle, getConnInfo } from 'hono/aws-lambda'
*
* const app = new Hono()
*
* app.get('/', (c) => {
*   const info = getConnInfo(c)
*   return c.text(`Your IP: ${info.remote.address}`)
* })
*
* export const handler = handle(app)
* ```
* @deprecated `hono/aws-lambda` will be removed in v5. Install `@hono/aws-lambda` and import from there instead.
*/
const getConnInfo = (c) => {
	const requestContext = c.env.requestContext;
	let address;
	if ("identity" in requestContext && requestContext.identity?.sourceIp) address = requestContext.identity.sourceIp;
	else if ("http" in requestContext && requestContext.http?.sourceIp) address = requestContext.http.sourceIp;
	else {
		const xff = c.req.header("x-forwarded-for");
		if (xff) {
			const ips = xff.split(",");
			address = ips[ips.length - 1].trim();
		}
	}
	return { remote: { address } };
};
//#endregion
exports.getConnInfo = getConnInfo;
