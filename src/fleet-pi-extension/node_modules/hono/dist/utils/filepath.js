//#region src/utils/filepath.ts
const getFilePath = (options) => {
	let filename = options.filename;
	const defaultDocument = options.defaultDocument || "index.html";
	if (filename.endsWith("/")) filename = filename.concat(defaultDocument);
	else if (!filename.match(/\.[a-zA-Z0-9_-]+$/)) filename = filename.concat("/" + defaultDocument);
	return getFilePathWithoutDefaultDocument({
		root: options.root,
		filename
	});
};
const getFilePathWithoutDefaultDocument = (options) => {
	let root = options.root || "";
	let filename = options.filename;
	if (/(?:^|[\/\\])\.\.(?:$|[\/\\])/.test(filename)) return;
	filename = filename.replace(/^\.?[\/\\]/, "");
	filename = filename.replace(/\\/g, "/");
	root = root.replace(/\/$/, "");
	let path = root ? root + "/" + filename : filename;
	path = path.replace(/^\.?\//, "");
	if (root[0] !== "/" && path[0] === "/") return;
	return path;
};
//#endregion
export { getFilePath, getFilePathWithoutDefaultDocument };
