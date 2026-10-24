Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_router = require("../../router.js");
//#region src/router/pattern-router/router.ts
const emptyParams = Object.create(null);
var PatternRouter = class {
	name = "PatternRouter";
	#routes = [];
	add(method, path, handler) {
		const suffix = path.endsWith("/*") ? "(?:$|/)" : path.endsWith("*") ? "" : "/?$";
		path = path.replace(/\*$/, "");
		if (path.at(-1) === "?") {
			path = path.slice(0, -1);
			this.add(method, path.replace(/\/[^/]+$/, ""), handler);
		}
		const parts = (path.match(/\/?(:\w+(?:{(?:(?:{[\d,]+})|[^}])+})?)|\/?[^\/\?]+/g) || []).map((part) => {
			const match = part.match(/^\/:([^{]+)(?:{(.*)})?/);
			return match ? `/(?<${match[1]}>${match[2] || "[^/]+"})` : part === "/*" ? "/[^/]+" : part.replace(/[.\\+*[^\]$()]/g, "\\$&");
		});
		try {
			this.#routes.push([
				new RegExp(`^${parts.join("")}${suffix}`),
				method,
				handler
			]);
		} catch {
			throw new require_router.UnsupportedPathError();
		}
	}
	match(method, path) {
		const handlers = [];
		for (let i = 0, len = this.#routes.length; i < len; i++) {
			const [pattern, routeMethod, handler] = this.#routes[i];
			if (routeMethod === method || routeMethod === "ALL") {
				const match = pattern.exec(path);
				if (match) handlers.push([handler, match.groups || emptyParams]);
			}
		}
		return [handlers];
	}
};
//#endregion
exports.PatternRouter = PatternRouter;
