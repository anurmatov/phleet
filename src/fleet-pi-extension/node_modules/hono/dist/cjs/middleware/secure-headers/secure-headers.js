Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_encode = require("../../utils/encode.js");
//#region src/middleware/secure-headers/secure-headers.ts
const HEADERS_MAP = {
	crossOriginEmbedderPolicy: ["Cross-Origin-Embedder-Policy", "require-corp"],
	crossOriginResourcePolicy: ["Cross-Origin-Resource-Policy", "same-origin"],
	crossOriginOpenerPolicy: ["Cross-Origin-Opener-Policy", "same-origin"],
	originAgentCluster: ["Origin-Agent-Cluster", "?1"],
	referrerPolicy: ["Referrer-Policy", "no-referrer"],
	strictTransportSecurity: ["Strict-Transport-Security", "max-age=15552000; includeSubDomains"],
	xContentTypeOptions: ["X-Content-Type-Options", "nosniff"],
	xDnsPrefetchControl: ["X-DNS-Prefetch-Control", "off"],
	xDownloadOptions: ["X-Download-Options", "noopen"],
	xFrameOptions: ["X-Frame-Options", "SAMEORIGIN"],
	xPermittedCrossDomainPolicies: ["X-Permitted-Cross-Domain-Policies", "none"],
	xXssProtection: ["X-XSS-Protection", "0"]
};
const DEFAULT_OPTIONS = {
	crossOriginEmbedderPolicy: false,
	crossOriginResourcePolicy: true,
	crossOriginOpenerPolicy: true,
	originAgentCluster: true,
	referrerPolicy: true,
	strictTransportSecurity: true,
	xContentTypeOptions: true,
	xDnsPrefetchControl: true,
	xDownloadOptions: true,
	xFrameOptions: true,
	xPermittedCrossDomainPolicies: true,
	xXssProtection: true,
	removePoweredBy: true,
	permissionsPolicy: {}
};
const generateNonce = () => {
	const arrayBuffer = /* @__PURE__ */ new Uint8Array(16);
	crypto.getRandomValues(arrayBuffer);
	return require_utils_encode.encodeBase64(arrayBuffer.buffer);
};
const NONCE = (ctx) => {
	const key = "secureHeadersNonce";
	const init = ctx.get(key);
	const nonce = init || generateNonce();
	if (init == null) ctx.set(key, nonce);
	return `'nonce-${nonce}'`;
};
/**
* Secure Headers Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/secure-headers}
*
* @param {Partial<SecureHeadersOptions>} [customOptions] - The options for the secure headers middleware.
* @param {ContentSecurityPolicyOptions} [customOptions.contentSecurityPolicy] - Settings for the Content-Security-Policy header.
* @param {ContentSecurityPolicyOptions} [customOptions.contentSecurityPolicyReportOnly] - Settings for the Content-Security-Policy-Report-Only header.
* @param {overridableHeader} [customOptions.crossOriginEmbedderPolicy=false] - Settings for the Cross-Origin-Embedder-Policy header.
* @param {overridableHeader} [customOptions.crossOriginResourcePolicy=true] - Settings for the Cross-Origin-Resource-Policy header.
* @param {overridableHeader} [customOptions.crossOriginOpenerPolicy=true] - Settings for the Cross-Origin-Opener-Policy header.
* @param {overridableHeader} [customOptions.originAgentCluster=true] - Settings for the Origin-Agent-Cluster header.
* @param {overridableHeader} [customOptions.referrerPolicy=true] - Settings for the Referrer-Policy header.
* @param {ReportingEndpointOptions[]} [customOptions.reportingEndpoints] - Settings for the Reporting-Endpoints header.
* @param {ReportToOptions[]} [customOptions.reportTo] - Settings for the Report-To header.
* @param {overridableHeader} [customOptions.strictTransportSecurity=true] - Settings for the Strict-Transport-Security header.
* @param {overridableHeader} [customOptions.xContentTypeOptions=true] - Settings for the X-Content-Type-Options header.
* @param {overridableHeader} [customOptions.xDnsPrefetchControl=true] - Settings for the X-DNS-Prefetch-Control header.
* @param {overridableHeader} [customOptions.xDownloadOptions=true] - Settings for the X-Download-Options header.
* @param {overridableHeader} [customOptions.xFrameOptions=true] - Settings for the X-Frame-Options header.
* @param {overridableHeader} [customOptions.xPermittedCrossDomainPolicies=true] - Settings for the X-Permitted-Cross-Domain-Policies header.
* @param {overridableHeader} [customOptions.xXssProtection=true] - Settings for the X-XSS-Protection header.
* @param {boolean} [customOptions.removePoweredBy=true] - Settings for remove X-Powered-By header.
* @param {PermissionsPolicyOptions} [customOptions.permissionsPolicy] - Settings for the Permissions-Policy header.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
* app.use(secureHeaders())
* ```
*/
const secureHeaders = (customOptions) => {
	const options = {
		...DEFAULT_OPTIONS,
		...customOptions
	};
	const headersToSet = getFilteredHeaders(options);
	const callbacks = [];
	if (options.contentSecurityPolicy) {
		const [callback, value] = getCSPDirectives(options.contentSecurityPolicy, "Content-Security-Policy");
		if (callback) callbacks.push(callback);
		headersToSet.push(["Content-Security-Policy", value]);
	}
	if (options.contentSecurityPolicyReportOnly) {
		const [callback, value] = getCSPDirectives(options.contentSecurityPolicyReportOnly, "Content-Security-Policy-Report-Only");
		if (callback) callbacks.push(callback);
		headersToSet.push(["Content-Security-Policy-Report-Only", value]);
	}
	if (options.permissionsPolicy && Object.keys(options.permissionsPolicy).length > 0) headersToSet.push(["Permissions-Policy", getPermissionsPolicyDirectives(options.permissionsPolicy)]);
	if (options.reportingEndpoints) headersToSet.push(["Reporting-Endpoints", getReportingEndpoints(options.reportingEndpoints)]);
	if (options.reportTo) headersToSet.push(["Report-To", getReportToOptions(options.reportTo)]);
	return async function secureHeaders(ctx, next) {
		const headersToSetForReq = callbacks.length === 0 ? headersToSet : callbacks.reduce((acc, cb) => cb(ctx, acc), headersToSet);
		await next();
		setHeaders(ctx, headersToSetForReq);
		if (options?.removePoweredBy) ctx.res.headers.delete("X-Powered-By");
	};
};
function getFilteredHeaders(options) {
	return Object.entries(HEADERS_MAP).filter(([key]) => options[key]).map(([key, defaultValue]) => {
		const overrideValue = options[key];
		return typeof overrideValue === "string" ? [defaultValue[0], overrideValue] : defaultValue;
	});
}
function getCSPDirectives(contentSecurityPolicy, headerName) {
	const callbacks = [];
	const resultValues = [];
	for (const [directive, value] of Object.entries(contentSecurityPolicy)) {
		const valueArray = Array.isArray(value) ? value : [value];
		valueArray.forEach((value, i) => {
			if (typeof value === "function") {
				const index = i * 2 + 2 + resultValues.length;
				callbacks.push((ctx, values) => {
					values[index] = value(ctx, directive);
				});
			}
		});
		resultValues.push(directive.replace(/[A-Z]+(?![a-z])|[A-Z]/g, (match, offset) => offset ? "-" + match.toLowerCase() : match.toLowerCase()), ...valueArray.flatMap((value) => [" ", value]), "; ");
	}
	resultValues.pop();
	return callbacks.length === 0 ? [void 0, resultValues.join("")] : [(ctx, headersToSet) => headersToSet.map((values) => {
		if (values[0] === headerName) {
			const clone = values[1].slice();
			callbacks.forEach((cb) => {
				cb(ctx, clone);
			});
			return [values[0], clone.join("")];
		} else return values;
	}), resultValues];
}
function getPermissionsPolicyDirectives(policy) {
	return Object.entries(policy).map(([directive, value]) => {
		const kebabDirective = camelToKebab(directive);
		if (typeof value === "boolean") return `${kebabDirective}=${value ? "*" : "()"}`;
		if (Array.isArray(value)) {
			if (value.length === 0) return `${kebabDirective}=()`;
			if (value.length === 1 && value[0] === "*") return `${kebabDirective}=*`;
			if (value.length === 1 && value[0] === "none") return `${kebabDirective}=()`;
			return `${kebabDirective}=(${value.map((item) => ["self", "src"].includes(item) ? item : `"${item}"`).join(" ")})`;
		}
		return "";
	}).filter(Boolean).join(", ");
}
function camelToKebab(str) {
	return str.replace(/([a-z\d])([A-Z])/g, "$1-$2").toLowerCase();
}
function getReportingEndpoints(reportingEndpoints = []) {
	return reportingEndpoints.map((endpoint) => `${endpoint.name}="${endpoint.url}"`).join(", ");
}
function getReportToOptions(reportTo = []) {
	return reportTo.map((option) => JSON.stringify(option)).join(", ");
}
function setHeaders(ctx, headersToSet) {
	headersToSet.forEach(([header, value]) => {
		ctx.res.headers.set(header, value);
	});
}
//#endregion
exports.NONCE = NONCE;
exports.secureHeaders = secureHeaders;
