Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/helper/streaming/utils.ts
let isOldBunVersion = () => {
	const version = typeof Bun !== "undefined" ? Bun.version : void 0;
	if (version === void 0) return false;
	const result = version.startsWith("1.1") || version.startsWith("1.0") || version.startsWith("0.");
	isOldBunVersion = () => result;
	return result;
};
//#endregion
Object.defineProperty(exports, "isOldBunVersion", {
	enumerable: true,
	get: function() {
		return isOldBunVersion;
	}
});
