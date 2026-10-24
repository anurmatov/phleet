Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_hono_base = require("../hono-base.js");
const require_router_pattern_router_router = require("../router/pattern-router/router.js");
//#region src/preset/tiny.ts
/**
* @module
* The preset that uses `PatternRouter`.
*/
var Hono = class extends require_hono_base.HonoBase {
	constructor(options = {}) {
		super(options);
		this.router = new require_router_pattern_router_router.PatternRouter();
	}
};
//#endregion
exports.Hono = Hono;
