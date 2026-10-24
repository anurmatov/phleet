Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_url = require("../../utils/url.js");
const require_router = require("../../router.js");
const require_router_utils = require("../utils.js");
const require_router_reg_exp_router_matcher = require("./matcher.js");
const require_router_reg_exp_router_node = require("./node.js");
const require_router_reg_exp_router_trie = require("./trie.js");
//#region src/router/reg-exp-router/router.ts
let wildcardRegExpCache = require_router_utils.createNullObject();
function buildWildcardRegExp(path) {
	return wildcardRegExpCache[path] ??= new RegExp(`^${path.replace(/\/:[^/{}]+(?:\{\[\^\/]\+})?(?=[/{]|$)|\/?\*$|([.\\+*[^\]$()?{}|])/g, (match, metaChar) => metaChar ? `\\${metaChar}` : match === "/*" ? require_router_reg_exp_router_node.TAIL_WILDCARD_REG_EXP_STR : match === "*" ? ".*" : `/:${require_router_reg_exp_router_node.LABEL_REG_EXP_STR}`)}$`);
}
function findMiddleware(middleware, path) {
	for (const k of Object.keys(middleware).sort((a, b) => b.length - a.length)) if (buildWildcardRegExp(k).test(path)) return [...middleware[k]];
}
var RegExpRouter = class {
	name = "RegExpRouter";
	#middleware;
	#routes;
	#tries;
	constructor() {
		this.#middleware = { ["ALL"]: require_router_utils.createNullObject() };
		this.#routes = { ["ALL"]: require_router_utils.createNullObject() };
		this.#tries = { ["ALL"]: new require_router_reg_exp_router_trie.Trie() };
	}
	#insertPath(method, path) {
		try {
			this.#tries[method].insert(path, !/\*|\/:/.test(path));
		} catch (e) {
			throw e === require_router_reg_exp_router_node.PATH_ERROR ? new require_router.UnsupportedPathError(path) : e;
		}
	}
	add(method, path, handler) {
		const middleware = this.#middleware;
		const routes = this.#routes;
		if (!middleware) throw new Error(require_router.MESSAGE_MATCHER_IS_ALREADY_BUILT);
		if (!middleware[method]) {
			this.#tries[method] = new require_router_reg_exp_router_trie.Trie();
			for (const handlerMap of [middleware, routes]) {
				handlerMap[method] = require_router_utils.createNullObject();
				for (const p in handlerMap["ALL"]) {
					handlerMap[method][p] = [...handlerMap["ALL"][p]];
					this.#insertPath(method, p);
				}
			}
		}
		if (path === "/*") path = "*";
		const methods = method === "ALL" ? Object.keys(middleware) : [method];
		if (/\*$/.test(path)) {
			const re = buildWildcardRegExp(path);
			for (const m of methods) if (!middleware[m][path]) {
				this.#insertPath(m, path);
				middleware[m][path] = findMiddleware(middleware[m], path) || findMiddleware(middleware["ALL"], path) || [];
			}
			for (const handlerMap of [middleware, routes]) for (const m of methods) for (const p in handlerMap[m]) re.test(p) && handlerMap[m][p].push([handler, path]);
			return;
		}
		const paths = require_utils_url.checkOptionalParameter(path) || [path];
		for (const path of paths) for (const m of methods) {
			if (!routes[m][path]) {
				this.#insertPath(m, path);
				routes[m][path] = findMiddleware(middleware[m], path) || findMiddleware(middleware["ALL"], path) || [];
			}
			routes[m][path].push([handler, path]);
		}
	}
	match = require_router_reg_exp_router_matcher.match;
	buildAllMatchers() {
		const matchers = require_router_utils.createNullObject();
		for (const method of Object.keys(this.#routes)) matchers[method] = this.#buildMatcher(method);
		this.#middleware = this.#routes = this.#tries = void 0;
		wildcardRegExpCache = require_router_utils.createNullObject();
		return matchers;
	}
	#buildMatcher(method) {
		const middleware = this.#middleware[method];
		const routes = this.#routes[method];
		const trie = this.#tries[method];
		const staticMap = require_router_utils.createNullObject();
		const handlerData = [];
		const [regexp, indexReplacementMap, paramReplacementMap] = trie.buildRegExp();
		for (const r of [middleware, routes]) for (const path in r) {
			const handlers = r[path];
			const pathData = trie.paths[path];
			if (!pathData) {
				staticMap[path] = [handlers.map(([h]) => [h, require_router_utils.createNullObject()]), require_router_reg_exp_router_matcher.emptyParam];
				continue;
			}
			handlerData[pathData[0]] = handlers.map(([h, handlerPath]) => [h, trie.paths[handlerPath][1].reduceRight((map, [key], i) => {
				map[key] = paramReplacementMap[pathData[1][i][1]];
				return map;
			}, require_router_utils.createNullObject())]);
		}
		return [
			regexp,
			indexReplacementMap.map((i) => handlerData[i]),
			staticMap
		];
	}
};
//#endregion
exports.RegExpRouter = RegExpRouter;
