Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_url = require("../../utils/url.js");
const require_router_trie_router_node = require("./node.js");
//#region src/router/trie-router/router.ts
var TrieRouter = class {
	name = "TrieRouter";
	#node = new require_router_trie_router_node.Node();
	add(method, path, handler) {
		for (const result of require_utils_url.checkOptionalParameter(path) || [path]) this.#node.insert(method, result, handler);
	}
	match(method, path) {
		return this.#node.search(method, path);
	}
};
//#endregion
exports.TrieRouter = TrieRouter;
