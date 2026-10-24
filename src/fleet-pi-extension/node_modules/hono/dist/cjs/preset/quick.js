Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_hono_base = require("../hono-base.js");
const require_router_smart_router_router = require("../router/smart-router/router.js");
const require_router_trie_router_router = require("../router/trie-router/router.js");
require("../router/trie-router/index.js");
const require_router_linear_router_router = require("../router/linear-router/router.js");
//#region src/preset/quick.ts
/**
* @module
* The preset that uses `LinearRouter`.
*/
var Hono = class extends require_hono_base.HonoBase {
	constructor(options = {}) {
		super(options);
		this.router = new require_router_smart_router_router.SmartRouter({ routers: [new require_router_linear_router_router.LinearRouter(), new require_router_trie_router_router.TrieRouter()] });
	}
};
//#endregion
exports.Hono = Hono;
