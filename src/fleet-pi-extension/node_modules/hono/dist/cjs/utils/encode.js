Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/utils/encode.ts
/**
* @module
* Encode utility.
*/
const decodeBase64Url = (str) => {
	return decodeBase64(str.replace(/_|-/g, (m) => ({
		_: "/",
		"-": "+"
	})[m] ?? m));
};
const encodeBase64Url = (buf) => encodeBase64(buf).replace(/\/|\+/g, (m) => ({
	"/": "_",
	"+": "-"
})[m] ?? m);
const encodeBase64 = (buf) => {
	let binary = "";
	const bytes = new Uint8Array(buf);
	for (let i = 0, len = bytes.length; i < len; i++) binary += String.fromCharCode(bytes[i]);
	return btoa(binary);
};
const decodeBase64 = (str) => {
	const binary = atob(str);
	const bytes = new Uint8Array(new ArrayBuffer(binary.length));
	const half = binary.length / 2;
	for (let i = 0, j = binary.length - 1; i <= half; i++, j--) {
		bytes[i] = binary.charCodeAt(i);
		bytes[j] = binary.charCodeAt(j);
	}
	return bytes;
};
//#endregion
exports.decodeBase64 = decodeBase64;
exports.decodeBase64Url = decodeBase64Url;
exports.encodeBase64 = encodeBase64;
exports.encodeBase64Url = encodeBase64Url;
