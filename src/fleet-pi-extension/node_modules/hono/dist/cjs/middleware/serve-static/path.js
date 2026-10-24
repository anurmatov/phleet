Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/middleware/serve-static/path.ts
/**
* `defaultJoin` does not support Windows paths and always uses `/` separators.
* If you need Windows path support, please use `join` exported from `node:path` etc. instead.
*/
const defaultJoin = (...paths) => {
	let result = paths.filter((p) => p !== "").join("/");
	result = result.replace(/(?<=\/)\/+/g, "");
	const segments = result.split("/");
	const resolved = [];
	for (const segment of segments) if (segment === ".." && resolved.length > 0 && resolved.at(-1) !== "..") resolved.pop();
	else if (segment !== ".") resolved.push(segment);
	return resolved.join("/") || ".";
};
//#endregion
exports.defaultJoin = defaultJoin;
