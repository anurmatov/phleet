import { HonoBase as Hono$1 } from "../hono-base.js";
import { SmartRouter } from "../router/smart-router/router.js";
import { TrieRouter } from "../router/trie-router/router.js";
import "../router/trie-router/index.js";
import { LinearRouter } from "../router/linear-router/router.js";
//#region src/preset/quick.ts
/**
* @module
* The preset that uses `LinearRouter`.
*/
var Hono = class extends Hono$1 {
	constructor(options = {}) {
		super(options);
		this.router = new SmartRouter({ routers: [new LinearRouter(), new TrieRouter()] });
	}
};
//#endregion
export { Hono };
