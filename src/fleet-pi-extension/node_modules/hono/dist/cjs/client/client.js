Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_cookie = require("../utils/cookie.js");
const require_client_utils = require("./utils.js");
//#region src/client/client.ts
const createProxy = (callback, path) => {
	return new Proxy(() => {}, {
		get(_obj, key) {
			if (typeof key !== "string" || key === "then") return;
			return createProxy(callback, [...path, key]);
		},
		apply(_1, _2, args) {
			return callback({
				path,
				args
			});
		}
	});
};
const appendQueryParams = (url, searchParams) => {
	const queryString = searchParams.toString();
	return queryString ? `${url}?${queryString}` : url;
};
var ClientRequestImpl = class {
	url;
	method;
	buildSearchParams;
	queryParams = void 0;
	pathParams = {};
	rBody;
	cType = void 0;
	constructor(url, method, options) {
		this.url = url;
		this.method = method;
		this.buildSearchParams = options.buildSearchParams;
	}
	fetch = async (args, opt) => {
		if (args) {
			if (args.query) this.queryParams = this.buildSearchParams(args.query);
			if (args.form) {
				const form = new FormData();
				for (const [k, v] of Object.entries(args.form)) {
					if (v === void 0) continue;
					if (Array.isArray(v)) for (const v2 of v) {
						if (v2 === void 0) continue;
						form.append(k, v2);
					}
					else form.append(k, v);
				}
				this.rBody = form;
			}
			if (args.json !== void 0) {
				this.rBody = JSON.stringify(args.json);
				this.cType = "application/json";
			}
			if (args.param) this.pathParams = args.param;
		}
		let methodUpperCase = this.method.toUpperCase();
		const headerValues = {
			...args?.header,
			...typeof opt?.headers === "function" ? await opt.headers() : opt?.headers
		};
		if (args?.cookie) {
			const cookies = [];
			for (const [key, value] of Object.entries(args.cookie)) {
				if (value === void 0) continue;
				cookies.push(require_utils_cookie.serialize(key, value));
			}
			if (cookies.length > 0) headerValues["Cookie"] = cookies.join("; ");
		}
		if (this.cType) headerValues["Content-Type"] = this.cType;
		const headers = new Headers();
		for (const [key, value] of Object.entries(headerValues)) if (value !== void 0) headers.set(key, value);
		let url = this.url;
		url = require_client_utils.removeIndexString(url);
		url = require_client_utils.replaceUrlParam(url, this.pathParams);
		if (this.queryParams) url = appendQueryParams(url, this.queryParams);
		methodUpperCase = this.method.toUpperCase();
		const setBody = !(methodUpperCase === "GET" || methodUpperCase === "HEAD");
		return (opt?.fetch || fetch)(url, {
			body: setBody ? this.rBody : void 0,
			method: methodUpperCase,
			headers,
			...opt?.init
		});
	};
};
const hc = (baseUrl, options) => createProxy(function proxyCallback(opts) {
	const buildSearchParamsOption = options?.buildSearchParams ?? require_client_utils.buildSearchParams;
	const parts = [...opts.path];
	const lastParts = parts.slice(-3).reverse();
	if (lastParts[0] === "toString") {
		if (lastParts[1] === "name") return lastParts[2] || "";
		return proxyCallback.toString();
	}
	if (lastParts[0] === "valueOf") {
		if (lastParts[1] === "name") return lastParts[2] || "";
		return proxyCallback;
	}
	let method = "";
	if (/^\$/.test(lastParts[0])) {
		const last = parts.pop();
		if (last) method = last.replace(/^\$/, "");
	}
	const path = parts.join("/");
	const url = require_client_utils.mergePath(baseUrl, path);
	if (method === "url" || method === "path") {
		let result = require_client_utils.removeIndexString(url);
		if (opts.args[0]) {
			if (opts.args[0].param) result = require_client_utils.replaceUrlParam(result, opts.args[0].param);
			if (opts.args[0].query) result = appendQueryParams(result, buildSearchParamsOption(opts.args[0].query));
		}
		if (method === "url") return new URL(result);
		return result.slice(baseUrl.replace(/\/+$/, "").length).replace(/^\/?/, "/");
	}
	if (method === "ws") {
		const normalizedUrl = require_client_utils.removeIndexString(url);
		const webSocketUrl = require_client_utils.replaceUrlProtocol(opts.args[0]?.param ? require_client_utils.replaceUrlParam(normalizedUrl, opts.args[0].param) : normalizedUrl, "ws");
		const targetUrl = new URL(webSocketUrl);
		const queryParams = opts.args[0]?.query;
		if (queryParams) buildSearchParamsOption(queryParams).forEach((value, key) => {
			targetUrl.searchParams.append(key, value);
		});
		const establishWebSocket = (...args) => {
			if (options?.webSocket !== void 0 && typeof options.webSocket === "function") return options.webSocket(...args);
			return new WebSocket(...args);
		};
		return establishWebSocket(targetUrl.toString());
	}
	const req = new ClientRequestImpl(url, method, { buildSearchParams: buildSearchParamsOption });
	if (method) {
		options ??= {};
		const reqOptions = { ...opts.args[1] };
		const baseHeaders = options.headers;
		const reqHeaders = reqOptions.headers;
		if (baseHeaders && reqHeaders) reqOptions.headers = async () => ({
			...typeof baseHeaders === "function" ? await baseHeaders() : baseHeaders,
			...typeof reqHeaders === "function" ? await reqHeaders() : reqHeaders
		});
		const args = require_client_utils.deepMerge(options, reqOptions);
		return req.fetch(opts.args[0], args);
	}
	return req;
}, []);
//#endregion
exports.hc = hc;
