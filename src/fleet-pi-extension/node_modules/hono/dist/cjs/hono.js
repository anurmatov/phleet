Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_hono_base = require("./hono-base.js");
const require_router_reg_exp_router_router = require("./router/reg-exp-router/router.js");
require("./router/reg-exp-router/index.js");
const require_router_smart_router_router = require("./router/smart-router/router.js");
const require_router_trie_router_router = require("./router/trie-router/router.js");
require("./router/trie-router/index.js");
//#region src/hono.ts
/**
* The Hono class extends the functionality of the HonoBase class.
* It sets up routing and allows for custom options to be passed.
*
* @template E - The environment type.
* @template S - The schema type.
* @template BasePath - The base path type.
*/
var Hono = class extends require_hono_base.HonoBase {
	/**
	* Creates an instance of the Hono class.
	*
	* @param options - Optional configuration options for the Hono instance.
	*/
	constructor(options = {}) {
		super(options);
		this.router = options.router ?? new require_router_smart_router_router.SmartRouter({ routers: [new require_router_reg_exp_router_router.RegExpRouter(), new require_router_trie_router_router.TrieRouter()] });
	}
};
//#endregion
exports.Hono = Hono;
