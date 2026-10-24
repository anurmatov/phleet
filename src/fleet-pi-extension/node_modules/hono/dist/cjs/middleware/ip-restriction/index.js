Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_http_exception = require("../../http-exception.js");
const require_utils_ipaddr = require("../../utils/ipaddr.js");
//#region src/middleware/ip-restriction/index.ts
const IS_CIDR_NOTATION_REGEX = /\/[^/]*$/;
const parseCidrPrefix = (rule, prefix, max) => {
	if (!/^[0-9]{1,3}$/.test(prefix)) throw new TypeError(`Invalid rule: ${rule}`);
	const parsedPrefix = parseInt(prefix);
	if (parsedPrefix > max) throw new TypeError(`Invalid rule: ${rule}`);
	return parsedPrefix;
};
const buildMatcher = (rules) => {
	const functionRules = [];
	const staticRules = /* @__PURE__ */ new Set();
	const staticIPv4Rules = /* @__PURE__ */ new Set();
	const staticIPv6Rules = /* @__PURE__ */ new Set();
	const cidrRules = [];
	const registerStaticRule = (rule) => {
		const type = require_utils_ipaddr.distinctRemoteAddr(rule);
		if (type === void 0) throw new TypeError(`Invalid rule: ${rule}`);
		if (type === "IPv4") {
			const ipv4binary = require_utils_ipaddr.convertIPv4ToBinary(rule);
			staticRules.add(rule);
			staticRules.add(`::ffff:${rule}`);
			staticIPv4Rules.add(ipv4binary);
			staticIPv6Rules.add(65535n << 32n | ipv4binary);
		} else {
			const ipv6binary = require_utils_ipaddr.convertIPv6ToBinary(rule);
			const ipv6Addr = require_utils_ipaddr.convertIPv6BinaryToString(ipv6binary);
			staticRules.add(ipv6Addr);
			staticIPv6Rules.add(ipv6binary);
			if (require_utils_ipaddr.isIPv4MappedIPv6(ipv6binary)) {
				staticRules.add(ipv6Addr.substring(7));
				staticIPv4Rules.add(require_utils_ipaddr.convertIPv4MappedIPv6ToIPv4(ipv6binary));
			}
		}
	};
	for (let rule of rules) if (rule === "*") return () => true;
	else if (typeof rule === "function") functionRules.push(rule);
	else {
		if (IS_CIDR_NOTATION_REGEX.test(rule)) {
			const separatedRule = rule.split("/");
			const addrStr = separatedRule[0];
			const type = require_utils_ipaddr.distinctRemoteAddr(addrStr);
			if (type === void 0) throw new TypeError(`Invalid rule: ${rule}`);
			let isIPv4 = type === "IPv4";
			let prefix = parseCidrPrefix(rule, separatedRule[1], isIPv4 ? 32 : 128);
			if (isIPv4 ? prefix === 32 : prefix === 128) rule = addrStr;
			else {
				let addr = (isIPv4 ? require_utils_ipaddr.convertIPv4ToBinary : require_utils_ipaddr.convertIPv6ToBinary)(addrStr);
				if (type === "IPv6" && require_utils_ipaddr.isIPv4MappedIPv6(addr) && prefix >= 96) {
					isIPv4 = true;
					addr = require_utils_ipaddr.convertIPv4MappedIPv6ToIPv4(addr);
					prefix -= 96;
				}
				const mask = (1n << BigInt(prefix)) - 1n << BigInt((isIPv4 ? 32 : 128) - prefix);
				cidrRules.push([
					isIPv4,
					addr & mask,
					mask
				]);
				continue;
			}
		}
		registerStaticRule(rule);
	}
	return (remote) => {
		if (staticRules.has(remote.addr)) return true;
		const remoteAddr = remote.binaryAddr ||= (remote.isIPv4 ? require_utils_ipaddr.convertIPv4ToBinary : require_utils_ipaddr.convertIPv6ToBinary)(remote.addr);
		const remoteIPv4Addr = remote.isIPv4 || require_utils_ipaddr.isIPv4MappedIPv6(remoteAddr) ? remote.isIPv4 ? remoteAddr : require_utils_ipaddr.convertIPv4MappedIPv6ToIPv4(remoteAddr) : void 0;
		if ((remote.isIPv4 ? staticIPv4Rules : staticIPv6Rules).has(remoteAddr)) return true;
		for (const [isIPv4, addr, mask] of cidrRules) {
			if (isIPv4) {
				if (remoteIPv4Addr === void 0) continue;
				if ((remoteIPv4Addr & mask) === addr) return true;
				continue;
			}
			if (remote.isIPv4) continue;
			if ((remoteAddr & mask) === addr) return true;
		}
		for (const rule of functionRules) if (rule({
			addr: remote.addr,
			type: remote.type
		})) return true;
		return false;
	};
};
/**
* IP Restriction Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/ip-restriction}
*
* @param {GetConnInfo | ((c: Context) => string)} getIP - A function to retrieve the client IP address. Use `getConnInfo` from the appropriate runtime adapter.
* @param {IPRestrictionRules} rules - An object with optional `denyList` and `allowList` arrays of IP rules. Each rule can be a static IP, a CIDR range, or a custom function.
* @param {(remote: { addr: string; type: AddressType }, c: Context) => Response | Promise<Response>} [onError] - Optional custom handler invoked when a request is blocked. Defaults to returning a 403 Forbidden response.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* import { Hono } from 'hono'
* import { ipRestriction } from 'hono/ip-restriction'
* import { getConnInfo } from 'hono/cloudflare-workers'
*
* const app = new Hono()
*
* app.use(
*   '*',
*   ipRestriction(getConnInfo, {
*     // Block a specific IP and an entire subnet
*     denyList: ['192.168.0.5', '10.0.0.0/8'],
*     // Only allow requests from localhost and a private range
*     allowList: ['127.0.0.1', '::1', '192.168.1.0/24'],
*   })
* )
*
* // With a custom error handler
* app.use(
*   '/admin/*',
*   ipRestriction(
*     getConnInfo,
*     { allowList: ['203.0.113.0/24'] },
*     (remote, c) => c.text(`Access denied for ${remote.addr}`, 403)
*   )
* )
*
* app.get('/', (c) => c.text('Hello!'))
* ```
*/
const ipRestriction = (getIP, { denyList = [], allowList = [] }, onError) => {
	const allowLength = allowList.length;
	const denyMatcher = buildMatcher(denyList);
	const allowMatcher = buildMatcher(allowList);
	const blockError = (c) => new require_http_exception.HTTPException(403, { res: c.text("Forbidden", { status: 403 }) });
	return async function ipRestriction(c, next) {
		const connInfo = getIP(c);
		const addr = typeof connInfo === "string" ? connInfo : connInfo.remote.address;
		if (!addr) throw blockError(c);
		const type = typeof connInfo !== "string" && connInfo.remote.addressType || require_utils_ipaddr.distinctRemoteAddr(addr);
		const remoteData = {
			addr,
			type,
			isIPv4: type === "IPv4"
		};
		try {
			if (denyMatcher(remoteData)) {
				if (onError) return onError({
					addr,
					type
				}, c);
				throw blockError(c);
			}
			if (allowMatcher(remoteData)) return await next();
		} catch (e) {
			if (e instanceof TypeError && e.code === "ERR_INVALID_IP_ADDRESS") throw blockError(c);
			throw e;
		}
		if (allowLength === 0) return await next();
		else {
			if (onError) return await onError({
				addr,
				type
			}, c);
			throw blockError(c);
		}
	};
};
//#endregion
exports.ipRestriction = ipRestriction;
