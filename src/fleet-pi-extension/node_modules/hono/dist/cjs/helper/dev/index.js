Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_color = require("../../utils/color.js");
const require_utils_handler = require("../../utils/handler.js");
//#region src/helper/dev/index.ts
const handlerName = (handler) => {
	return handler.name || (require_utils_handler.isMiddleware(handler) ? "[middleware]" : "[handler]");
};
const inspectRoutes = (hono) => {
	return hono.routes.map(({ path, method, handler }) => {
		const targetHandler = require_utils_handler.findTargetHandler(handler);
		return {
			path,
			method,
			name: handlerName(targetHandler),
			isMiddleware: require_utils_handler.isMiddleware(targetHandler)
		};
	});
};
const showRoutes = (hono, opts) => {
	const colorEnabled = opts?.colorize ?? require_utils_color.getColorEnabled();
	const routeData = {};
	let maxMethodLength = 0;
	let maxPathLength = 0;
	inspectRoutes(hono).filter(({ isMiddleware }) => opts?.verbose || !isMiddleware).map((route) => {
		const key = `${route.method}-${route.path}`;
		(routeData[key] ||= []).push(route);
		if (routeData[key].length > 1) return;
		maxMethodLength = Math.max(maxMethodLength, route.method.length);
		maxPathLength = Math.max(maxPathLength, route.path.length);
		return {
			method: route.method,
			path: route.path,
			routes: routeData[key]
		};
	}).forEach((data) => {
		if (!data) return;
		const { method, path, routes } = data;
		const methodStr = colorEnabled ? `\x1b[32m${method}\x1b[0m` : method;
		console.log(`${methodStr} ${" ".repeat(maxMethodLength - method.length)} ${path}`);
		if (!opts?.verbose) return;
		routes.forEach(({ name }) => {
			console.log(`${" ".repeat(maxMethodLength + 3)} ${name}`);
		});
	});
};
const getRouterName = (app) => {
	app.router.match("GET", "/");
	return app.router.name;
};
//#endregion
exports.getRouterName = getRouterName;
exports.inspectRoutes = inspectRoutes;
exports.showRoutes = showRoutes;
