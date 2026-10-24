Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_url = require("../../utils/url.js");
const require_router = require("../../router.js");
//#region src/router/linear-router/router.ts
const emptyParams = Object.create(null);
const splitPathRe = /\/(:\w+(?:{(?:(?:{[\d,]+})|[^}])+})?)|\/[^\/\?]+|(\?)/g;
const splitByStarRe = /\*/;
var LinearRouter = class {
	name = "LinearRouter";
	#routes = [];
	add(method, path, handler) {
		for (let i = 0, paths = require_utils_url.checkOptionalParameter(path) || [path], len = paths.length; i < len; i++) this.#routes.push([
			method,
			paths[i],
			handler
		]);
	}
	match(method, path) {
		const handlers = [];
		ROUTES_LOOP: for (let i = 0, len = this.#routes.length; i < len; i++) {
			const [routeMethod, routePath, handler] = this.#routes[i];
			if (routeMethod === method || routeMethod === "ALL") {
				if (routePath === "*" || routePath === "/*") {
					handlers.push([handler, emptyParams]);
					continue;
				}
				const hasStar = routePath.indexOf("*") !== -1;
				const hasLabel = routePath.indexOf(":") !== -1;
				if (!hasStar && !hasLabel) {
					if (routePath === path || routePath + "/" === path) handlers.push([handler, emptyParams]);
				} else if (hasStar && !hasLabel) {
					const endsWithStar = routePath.charCodeAt(routePath.length - 1) === 42;
					const endsWithSlashStar = routePath.endsWith("/*");
					const parts = (endsWithStar ? routePath.slice(0, endsWithSlashStar ? -2 : -1) : routePath).split(splitByStarRe);
					const lastIndex = parts.length - 1;
					for (let j = 0, pos = 0, len = parts.length; j < len; j++) {
						const part = parts[j];
						if (path.indexOf(part, pos) !== pos) continue ROUTES_LOOP;
						pos += part.length;
						if (j === lastIndex) {
							if (endsWithSlashStar) {
								if (pos !== path.length && path.charCodeAt(pos) !== 47) continue ROUTES_LOOP;
							} else if (!endsWithStar && pos !== path.length && !(pos === path.length - 1 && path.charCodeAt(pos) === 47)) continue ROUTES_LOOP;
						} else {
							const index = path.indexOf("/", pos);
							if (index === -1) continue ROUTES_LOOP;
							pos = index;
						}
					}
					handlers.push([handler, emptyParams]);
				} else if (hasLabel && !hasStar) {
					const params = Object.create(null);
					const parts = routePath.match(splitPathRe);
					const lastIndex = parts.length - 1;
					for (let j = 0, pos = 0, len = parts.length; j < len; j++) {
						if (pos === -1 || pos >= path.length) continue ROUTES_LOOP;
						const part = parts[j];
						if (part.charCodeAt(1) === 58) {
							if (path.charCodeAt(pos) !== 47) continue ROUTES_LOOP;
							let name = part.slice(2);
							let value;
							if (name.charCodeAt(name.length - 1) === 125) {
								const openBracePos = name.indexOf("{");
								const next = parts[j + 1];
								const lookahead = next && next[1] !== ":" && next[1] !== "*" ? `(?=${next})` : "";
								const pattern = name.slice(openBracePos + 1, -1) + lookahead;
								const restPath = path.slice(pos + 1);
								const match = new RegExp(pattern, "d").exec(restPath);
								if (!match || match.indices[0][0] !== 0 || match.indices[0][1] === 0) continue ROUTES_LOOP;
								name = name.slice(0, openBracePos);
								value = restPath.slice(...match.indices[0]);
								pos += match.indices[0][1] + 1;
							} else {
								let endValuePos = path.indexOf("/", pos + 1);
								if (endValuePos === -1) endValuePos = path.length;
								if (endValuePos === pos + 1) continue ROUTES_LOOP;
								value = path.slice(pos + 1, endValuePos);
								pos = endValuePos;
							}
							params[name] ||= value;
						} else {
							if (path.indexOf(part, pos) !== pos) continue ROUTES_LOOP;
							pos += part.length;
						}
						if (j === lastIndex) {
							if (pos !== path.length && !(pos === path.length - 1 && path.charCodeAt(pos) === 47)) continue ROUTES_LOOP;
						}
					}
					handlers.push([handler, params]);
				} else if (hasLabel && hasStar) throw new require_router.UnsupportedPathError();
			}
		}
		return [handlers];
	}
};
//#endregion
exports.LinearRouter = LinearRouter;
