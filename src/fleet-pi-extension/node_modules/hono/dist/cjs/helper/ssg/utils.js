Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
require("../../router.js");
const require_utils_handler = require("../../utils/handler.js");
//#region src/helper/ssg/utils.ts
/**
* Get dirname
* @param path File Path
* @returns Parent dir path
*/
const dirname = (path) => {
	return path.split(/[\/\\]/).slice(0, -1).join("/");
};
const normalizePath = (path) => {
	return path.replace(/(\\)/g, "/").replace(/\/$/g, "");
};
const getUncRoot = (path) => {
	const uncRoot = path.replace(/\\/g, "/").match(/^\/\/([^/]+)\/([^/]+)/);
	if (uncRoot) return `${uncRoot[1].toLowerCase()}/${uncRoot[2].toLowerCase()}`;
};
const handleParent = (resultPaths) => {
	if (resultPaths.length === 0 || resultPaths[resultPaths.length - 1] === "..") resultPaths.push("..");
	else resultPaths.pop();
};
const handleNonDot = (path, resultPaths) => {
	path = path.replace(/^\.(?!.)/, "");
	if (path !== "") resultPaths.push(path);
};
const handleSegments = (paths, resultPaths) => {
	for (const path of paths) if (path === "..") handleParent(resultPaths);
	else handleNonDot(path, resultPaths);
};
const joinPaths = (...paths) => {
	const hasUncPrefix = getUncRoot(paths[0]) !== void 0;
	paths = paths.map(normalizePath);
	const resultPaths = [];
	handleSegments(paths.join("/").split("/"), resultPaths);
	return (hasUncPrefix ? "//" : paths[0][0] === "/" ? "/" : "") + resultPaths.join("/");
};
const filterStaticGenerateRoutes = (hono) => {
	return hono.routes.reduce((acc, { method, handler, path }) => {
		const targetHandler = require_utils_handler.findTargetHandler(handler);
		if (["GET", "ALL"].includes(method) && !require_utils_handler.isMiddleware(targetHandler)) acc.push({ path });
		return acc;
	}, []);
};
const isDynamicRoute = (path) => {
	return path.split("/").some((segment) => segment.startsWith(":") || segment.includes("*"));
};
const toSegments = (path) => path === "" ? [] : path.split("/");
const getPathRoot = (path) => {
	const normalizedPath = path.replace(/\\/g, "/");
	const uncRoot = getUncRoot(normalizedPath);
	if (uncRoot) return `unc:${uncRoot}`;
	const driveRoot = normalizedPath.match(/^([A-Za-z]):/);
	if (driveRoot) return `${normalizedPath[2] === "/" ? "drive-absolute" : "drive-relative"}:${driveRoot[1].toLowerCase()}`;
	return normalizedPath.startsWith("/") ? "absolute" : "relative";
};
const ensureWithinOutDir = (outDir, filePath) => {
	const outDirSegments = toSegments(joinPaths(outDir));
	const filePathSegments = toSegments(joinPaths(filePath));
	const hasMismatchedPathRoot = getPathRoot(outDir) !== getPathRoot(filePath);
	const climbsAboveOutDir = filePathSegments[outDirSegments.length] === "..";
	if (hasMismatchedPathRoot || filePathSegments.length <= outDirSegments.length || !outDirSegments.every((segment, i) => segment === filePathSegments[i]) || climbsAboveOutDir) throw new Error(`Path traversal detected: "${filePath}" is outside the output directory`);
};
//#endregion
exports.dirname = dirname;
exports.ensureWithinOutDir = ensureWithinOutDir;
exports.filterStaticGenerateRoutes = filterStaticGenerateRoutes;
exports.isDynamicRoute = isDynamicRoute;
exports.joinPaths = joinPaths;
