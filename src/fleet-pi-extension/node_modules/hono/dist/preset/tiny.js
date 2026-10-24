import { HonoBase as Hono$1 } from "../hono-base.js";
import { PatternRouter } from "../router/pattern-router/router.js";
//#region src/preset/tiny.ts
/**
* @module
* The preset that uses `PatternRouter`.
*/
var Hono = class extends Hono$1 {
	constructor(options = {}) {
		super(options);
		this.router = new PatternRouter();
	}
};
//#endregion
export { Hono };
